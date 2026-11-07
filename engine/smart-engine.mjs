import http from 'node:http';
import dns from 'node:dns/promises';
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { performance } from 'node:perf_hooks';

const SECOND = 1000;
const MiB = 1024 * 1024;
const DEFAULTS = Object.freeze({
  dnsTtlMs: 60 * SECOND, historyTtlMs: 24 * 3600 * SECOND,
  scoreTtlMs: 5 * 60 * SECOND, scanIntervalMs: 5 * 60 * SECOND,
  probeTimeoutMs: 1800, probeMaxBytes: MiB, minimumProbeBytes: 64 * 1024,
  maxScanBytes: 32 * MiB, maxScanCandidates: 32,
  probeBudgetBytes: 128 * MiB, probeBudgetWindowMs: 15 * 60 * SECOND,
  firstByteTimeoutMs: 8000, idleTimeoutMs: 20000, failureBackoffMs: 15000,
  activeGraceMs: 5000, templateTtlMs: 5 * 60 * SECOND,
  maintenanceMs: SECOND, persistIntervalMs: 30 * SECOND,
});
const HOP_HEADERS = new Set(['connection', 'keep-alive', 'proxy-authenticate',
  'proxy-authorization', 'te', 'trailer', 'transfer-encoding', 'upgrade']);
const RETRY_STATUSES = new Set([403, 404, 408, 429, 500, 502, 503, 504]);

function cleanHeaders(headers) {
  const named = new Set(String(headers.connection || '').toLowerCase().split(',').map(x => x.trim()));
  return Object.fromEntries(Object.entries(headers).filter(([name]) => !HOP_HEADERS.has(name.toLowerCase()) && !named.has(name.toLowerCase())));
}
function addressKey(endpoint) { return `${endpoint.ip}|${endpoint.port}`; }
function safeError(error) {
  const code = String(error?.code || 'request_failed');
  return /^[A-Z0-9_]{1,40}$/.test(code) ? code : 'request_failed';
}
function isPublicIP(ip) {
  if (!net.isIP(ip)) return false;
  const value = ip.toLowerCase();
  if (net.isIPv4(ip)) {
    const [a, b] = ip.split('.').map(Number);
    return a !== 0 && a !== 10 && a !== 127 && a < 224 &&
      !(a === 169 && b === 254) && !(a === 172 && b >= 16 && b <= 31) &&
      !(a === 192 && b === 168) && !(a === 100 && b >= 64 && b <= 127);
  }
  if (value.startsWith('::ffff:')) return isPublicIP(value.slice(7));
  return value !== '::' && value !== '::1' && !/^f[cd]/.test(value) && !/^fe[89ab]/.test(value) && !value.startsWith('ff');
}
function parseTarget(target) {
  const url = new URL(String(target).includes('://') ? target : `http://${target}`);
  if (url.protocol !== 'http:' || url.username || url.password) throw new Error('Only HTTP upstream targets are supported.');
  return { hostname: url.hostname.replace(/^\[|\]$/g, ''), port: Number(url.port || 80) };
}
function requestPath(req, host) {
  if (!req.url || req.url.length > 65536) throw new Error('invalid_path');
  if (req.url.startsWith('/')) return req.url;
  const url = new URL(req.url);
  if (url.protocol !== 'http:' || url.hostname.toLowerCase() !== host || url.username || url.password) throw new Error('invalid_path');
  return url.pathname + url.search;
}
function depotOf(urlPath) { return /^\/depot\/(\d+)\/(?:chunk|patch)\//i.exec(urlPath)?.[1] || null; }
function binaryResponse(response) { return !/(?:text\/|json|xml|html|javascript)/i.test(String(response.headers['content-type'] || '')); }
function templateValid(template, timing) {
  if (!template || Date.now() - template.at > timing.templateTtlMs) return false;
  try {
    const query = new URL(template.path, 'http://memory.invalid').searchParams;
    for (const field of ['t', 'expiration_time', 'expires', 'expiration']) {
      const value = Number(query.get(field));
      if (value > 1_000_000_000 && value < Date.now() / 1000 + 10) return false;
    }
  } catch { return false; }
  return true;
}
function probeRange(original, maximum) {
  const match = /^bytes=(\d+)-(\d*)$/.exec(String(original || ''));
  const start = match ? Number(match[1]) : 0;
  const end = match && match[2] ? Math.min(Number(match[2]), start + maximum - 1) : start + maximum - 1;
  return `bytes=${start}-${end}`;
}

/** A loopback-only server owner should call handle(); this class never changes hosts. */
export class SteamSmartEngine {
  constructor(config, options = {}) {
    this.options = options;
    this.timing = { ...DEFAULTS, ...options.timing };
    this.closed = false;
    this.startedAt = Date.now();
    this.groups = new Map();
    this.hosts = new Map();
    this.redirects = new Map();
    this.events = [];
    this.sockets = new Set();
    this.transfers = new Set();
    this.queue = new Map();
    this.scanning = null;
    this.totalBytes = 0;
    this.probeBytes = 0;
    this.rateBuckets = new Map();
    this.budgetAt = Date.now();
    this.budgetBytes = 0;
    this.lastPersist = 0;
    this.agent = new http.Agent({ keepAlive: true, maxSockets: 16, maxFreeSockets: 4, timeout: 30000 });
    this.dns = new dns.Resolver({ timeout: 2500, tries: 1 });
    for (const item of config.groups || []) {
      const target = parseTarget(item.target);
      const group = { id: String(item.id), name: String(item.name || item.id), target,
        hosts: [...new Set(item.hosts.map(x => String(x).toLowerCase()))], entries: new Map(),
        dnsAt: 0, lastRequestAt: 0, lastScanAt: 0, lastScanDepot: null, lastScanHost: null, latestDepot: null,
        template: null, rr: 0, bytes: 0, rateBuckets: new Map(), resolving: null,
        scanStatus: 'waiting_for_download', scanCursor: 0, requestCount: 0, selectedKey: '', lastSwitchAt: 0 };
      if (!group.id || !group.hosts.length) throw new Error('Each group needs an id and hosts.');
      this.groups.set(group.id, group);
      for (const seed of item.seedIPs || []) {
        const ip = typeof seed === 'string' ? seed : seed.ip;
        if (net.isIP(ip) && (this.options.allowTestLocal || isPublicIP(ip))) {
          const entry = this._entry(group, { ip, port: target.port });
          entry.seedAt = Date.now();
          entry.seedOnly = true;
        }
      }
      for (const host of group.hosts) {
        if (!/^[a-z0-9.-]+$/.test(host) || this.hosts.has(host)) throw new Error('Invalid or duplicate Steam host.');
        this.hosts.set(host, group);
      }
    }
    for (const [from, to] of Object.entries(config.redirects || {})) {
      const dest = String(to).toLowerCase();
      if (!this.hosts.has(dest)) throw new Error('Redirect destination must be a configured Steam host.');
      this.redirects.set(from.toLowerCase(), dest);
    }
    this._loadState();
    this.timer = setInterval(() => this._maintenance(), this.timing.maintenanceMs);
    this.timer.unref();
  }

  _event(type, group = null, details = {}) {
    const event = { at: new Date().toISOString(), type, ...(group ? { group: group.id, name: group.name } : {}), ...details };
    this.events.push(event);
    if (this.events.length > 80) this.events.shift();
    try { this.options.onEvent?.(event); } catch { /* A display callback cannot stop forwarding. */ }
  }

  _entry(group, endpoint) {
    const key = addressKey(endpoint);
    let entry = group.entries.get(key);
    if (!entry) {
      entry = { ip: endpoint.ip, port: endpoint.port, bytes: 0, probeBytes: 0,
        lastDnsAt: 0, lastSuccessAt: 0, lastProbeAt: 0, scoreBps: null,
        lastResult: 'untested', lastStatus: null, failures: 0, retryAt: 0,
        validatedDepot: null, validatedHost: null, loaded: false, active: 0, rateBuckets: new Map() };
      group.entries.set(key, entry);
    }
    return entry;
  }

  async _resolve(group) {
    const found = this.options.resolver
      ? await this.options.resolver(group.target.hostname, { group: group.id, port: group.target.port })
      : (await Promise.allSettled([this.dns.resolve4(group.target.hostname), this.dns.resolve6(group.target.hostname)]))
        .flatMap(result => result.status === 'fulfilled' ? result.value : []);
    const map = new Map();
    for (const value of found || []) {
      const ip = typeof value === 'string' ? value : value.ip;
      const port = typeof value === 'string' ? group.target.port : Number(value.port || group.target.port);
      if ((!this.options.allowTestLocal && !isPublicIP(ip)) || !net.isIP(ip) || !Number.isInteger(port) || port < 1 || port > 65535) continue;
      map.set(addressKey({ ip, port }), { ip, port });
    }
    return [...map.values()];
  }

  async _ensurePool(group, force = false) {
    if (this.closed) return;
    if (group.resolving) return group.resolving;
    if (!force && Date.now() - group.dnsAt < this.timing.dnsTtlMs && group.entries.size) return;
    group.resolving = (async () => {
      const old = new Set([...group.entries.values()].filter(e => e.lastDnsAt === group.dnsAt).map(addressKey));
      try {
        const endpoints = await this._resolve(group);
        if (this.closed) return;
        if (!endpoints.length) throw Object.assign(new Error('No public DNS addresses.'), { code: 'DNS_EMPTY' });
        const now = Date.now();
        group.dnsAt = now;
        for (const endpoint of endpoints) this._entry(group, endpoint).lastDnsAt = now;
        const current = new Set(endpoints.map(addressKey));
        if (old.size !== current.size || [...current].some(key => !old.has(key))) {
          this._event('dns_changed', group, { addresses: current.size });
          group.lastScanAt = 0;
          this._scheduleScan(group, 'dns_changed');
        }
      } catch (error) {
        if (this.closed) return;
        group.dnsAt = Date.now() - this.timing.dnsTtlMs + Math.min(this.timing.dnsTtlMs, 15000);
        this._event('dns_retry', group, { reason: safeError(error) });
      } finally {
        const oldest = Date.now() - this.timing.historyTtlMs;
        for (const [key, entry] of group.entries) {
          if (Math.max(entry.lastDnsAt, entry.lastSuccessAt, entry.seedAt || 0) < oldest && !entry.active) group.entries.delete(key);
        }
        group.resolving = null;
      }
    })();
    return group.resolving;
  }

  _pool(group) {
    const oldest = Date.now() - this.timing.historyTtlMs;
    return [...group.entries.values()].filter(e => Math.max(e.lastDnsAt, e.lastSuccessAt, e.seedAt || 0) >= oldest);
  }

  _isActive(group) {
    return [...group.entries.values()].some(e => e.active > 0) || Date.now() - group.lastRequestAt < this.timing.activeGraceMs;
  }

  _preferred(group, host) {
    const now = Date.now();
    return this._pool(group).filter(e => e.retryAt <= now && !e.loaded &&
      e.lastResult === 'direct' && e.validatedDepot === group.latestDepot &&
      e.validatedHost === host && now - e.lastSuccessAt <= this.timing.scoreTtlMs && e.scoreBps > 0)
      .sort((a, b) => b.scoreBps - a.scoreBps);
  }

  _selectionPool(group, host, preferred = this._preferred(group, host)) {
    if (!preferred.length) return [];
    return preferred.filter(entry => entry.scoreBps >= preferred[0].scoreBps * 0.35).slice(0, 4);
  }

  _candidates(group, host) {
    const entries = this._pool(group);
    const now = Date.now();
    const preferred = this._preferred(group, host);
    if (preferred.length) {
      const top = this._selectionPool(group, host, preferred);
      const next = top[group.rr++ % top.length];
      return [next, ...preferred.filter(e => e !== next), ...entries.filter(e => !preferred.includes(e))];
    }
    // Before validation, explore the pool instead of sticking to its first address.
    const ready = entries.filter(e => e.retryAt <= now && (!e.seedOnly || e.lastSuccessAt > now - this.timing.scoreTtlMs));
    const ordinary = ready.filter(e => !['redirect', 'unavailable'].includes(e.lastResult));
    const eligible = ordinary.length ? ordinary : ready.length ? ready : entries;
    if (!eligible.length) return [];
    const next = eligible[group.rr++ % eligible.length];
    return [next, ...eligible.filter(e => e !== next), ...entries.filter(e => !eligible.includes(e))];
  }

  _recordBytes(group, entry, bytes, probe = false) {
    if (probe) {
      this.probeBytes += bytes;
      entry.probeBytes += bytes;
      this.budgetBytes += bytes;
      return;
    }
    const second = Math.floor(Date.now() / SECOND);
    this.totalBytes += bytes;
    group.bytes += bytes;
    entry.bytes += bytes;
    for (const map of [this.rateBuckets, group.rateBuckets, entry.rateBuckets]) {
      map.set(second, (map.get(second) || 0) + bytes);
      for (const key of map.keys()) if (key < second - 10) map.delete(key);
    }
  }

  _rate(buckets) {
    const second = Math.floor(Date.now() / SECOND);
    let bytes = 0;
    for (const [key, value] of buckets) if (key > second - 5 && key <= second) bytes += value;
    return bytes / Math.min(5, Math.max(1, (Date.now() - this.startedAt) / SECOND));
  }

  _success(group, entry, { bytes, elapsedMs, host, depot, probe = false }) {
    if (bytes < this.timing.minimumProbeBytes || elapsedMs <= 0) return;
    const measured = bytes * SECOND / elapsedMs;
    entry.scoreBps = !entry.scoreBps || entry.loaded ? measured : entry.scoreBps * (probe ? 0.25 : 0.65) + measured * (probe ? 0.75 : 0.35);
    entry.lastSuccessAt = Date.now();
    entry.lastResult = 'direct';
    entry.lastStatus = 200;
    entry.failures = 0;
    entry.retryAt = 0;
    entry.validatedDepot = depot;
    entry.validatedHost = host;
    entry.loaded = false;
    const selected = this._selectionPool(group, host);
    const selectedKey = selected.map(addressKey).sort().join(',');
    if (selectedKey && selectedKey !== group.selectedKey) {
      group.selectedKey = selectedKey;
      this._event('selection_updated', group, { ips: selected.map(e => e.ip) });
    }
  }

  _failure(group, entry, reason, status = null) {
    entry.lastStatus = status;
    entry.lastResult = reason === 'redirect' ? 'redirect' : 'unavailable';
    entry.failures++;
    entry.retryAt = Date.now() + (reason === 'redirect' ? 0 : this.timing.failureBackoffMs);
  }

  async handle(req, res) {
    if (this.closed) { req.resume(); res.writeHead(503); res.end(); return; }
    const host = String(req.headers.host || '').toLowerCase().replace(/:80$/, '');
    if (!/^[a-z0-9.-]+$/.test(host) || (!this.hosts.has(host) && !this.redirects.has(host))) {
      req.resume(); res.writeHead(421); res.end('Unknown download host'); return;
    }
    let urlPath;
    try { urlPath = requestPath(req, host); }
    catch { req.resume(); res.writeHead(400); res.end('Invalid request'); return; }
    if (!['GET', 'POST', 'HEAD'].includes(req.method)) { req.resume(); res.writeHead(405, { Allow: 'GET, HEAD, POST' }); res.end(); return; }
    if (this.redirects.has(host)) {
      req.resume(); res.writeHead(['GET', 'HEAD'].includes(req.method) ? 302 : 307, { Location: `http://${this.redirects.get(host)}${urlPath}` }); res.end(); return;
    }
    const group = this.hosts.get(host);
    group.lastRequestAt = Date.now();
    group.requestCount++;
    const depot = depotOf(urlPath);
    const bodyless = !req.headers['transfer-encoding'] && (!req.headers['content-length'] || Number(req.headers['content-length']) === 0);
    const canReplay = ['GET', 'HEAD'].includes(req.method) && bodyless;
    const canProbe = req.method === 'GET' && bodyless && depot;
    if (canProbe) {
      group.latestDepot = depot;
      group.template = { host, path: urlPath, headers: { ...req.headers }, depot, at: Date.now() };
    }
    try {
      await this._ensurePool(group);
      if (req.destroyed || res.destroyed || this.closed) return;
      if (canProbe) this._scheduleScan(group, 'request');
      const candidates = this._candidates(group, host).slice(0, canReplay ? 3 : 1);
      if (!candidates.length) { req.resume(); res.writeHead(502); res.end('Download DNS temporarily unavailable'); return; }
      if (canReplay) req.resume();
      for (let index = 0; index < candidates.length; index++) {
        if (res.destroyed || this.closed) return;
        const retry = await this._forward(req, res, { group, entry: candidates[index], host, urlPath, depot, canReplay, retryAllowed: index < candidates.length - 1 });
        if (!retry) return;
      }
      if (!res.headersSent && !res.destroyed) { res.writeHead(502); res.end('Download upstream temporarily unavailable'); }
    } catch (error) {
      this._event('forward_retry', group, { reason: safeError(error) });
      req.resume();
      if (!res.headersSent && !res.destroyed) { res.writeHead(502); res.end('Download upstream temporarily unavailable'); }
      else if (!res.destroyed) res.destroy();
    }
  }

  _forward(req, res, context) {
    const { group, entry, host, urlPath, depot, canReplay, retryAllowed } = context;
    return new Promise(resolve => {
      const start = performance.now();
      let settled = false, received = false, bytes = 0, response = null, clientGone = false;
      entry.active++;
      const headers = cleanHeaders(req.headers);
      headers.host = req.headers.host;
      const outbound = http.request({ hostname: entry.ip, port: entry.port, method: req.method, path: urlPath, headers, agent: this.agent });
      this.sockets.add(outbound);
      const transfer = { group, entry, req, res, outbound, start, host, depot, canReplay, canSwitch: false, get bytes() { return bytes; } };
      this.transfers.add(transfer);
      const done = retry => {
        if (settled) return;
        settled = true;
        clearTimeout(firstByteTimer);
        entry.active--;
        this.sockets.delete(outbound);
        this.transfers.delete(transfer);
        req.off('aborted', abort);
        res.off('close', clientClose);
        resolve(retry);
      };
      const abort = () => { clientGone = true; outbound.destroy(); response?.destroy(); done(false); };
      const clientClose = () => { if (!res.writableFinished) abort(); };
      req.once('aborted', abort);
      res.once('close', clientClose);
      const firstByteTimer = setTimeout(() => outbound.destroy(Object.assign(new Error('first byte timeout'), { code: 'ETIMEDOUT' })), this.timing.firstByteTimeoutMs);
      outbound.setTimeout(this.timing.idleTimeoutMs, () => outbound.destroy(Object.assign(new Error('idle timeout'), { code: 'ETIMEDOUT' })));
      outbound.once('response', upstream => {
        response = upstream;
        received = true;
        clearTimeout(firstByteTimer);
        const status = upstream.statusCode || 502;
        const isDirect = [200, 206].includes(status);
        const isBinary = binaryResponse(upstream);
        transfer.canSwitch = req.method === 'GET' && isDirect && isBinary && Boolean(depot);
        if (status >= 300 && status < 400) this._failure(group, entry, 'redirect', status);
        else if (!isDirect) this._failure(group, entry, 'http_error', status);
        if (retryAllowed && canReplay && RETRY_STATUSES.has(status)) {
          upstream.destroy();
          outbound.destroy();
          done(true);
          return;
        }
        if (res.destroyed) { abort(); return; }
        res.writeHead(status, cleanHeaders(upstream.headers));
        upstream.on('data', chunk => { bytes += chunk.length; this._recordBytes(group, entry, chunk.length); });
        upstream.once('end', () => {
          if (isDirect && isBinary && depot) this._success(group, entry, { bytes, elapsedMs: performance.now() - start, host, depot });
          done(false);
        });
        upstream.once('error', () => {
          if (!clientGone) this._failure(group, entry, 'interrupted');
          if (!res.destroyed) res.destroy();
          done(false);
        });
        upstream.pipe(res);
      });
      outbound.once('error', error => {
        if (settled) return;
        if (!clientGone) this._failure(group, entry, safeError(error));
        if (res.headersSent) { if (!res.destroyed) res.destroy(); done(false); }
        else if (canReplay && retryAllowed && !clientGone) done(true);
        else {
          if (!clientGone && !res.destroyed) { res.writeHead(502); res.end('Download upstream temporarily unavailable'); }
          if (!canReplay) { req.unpipe(outbound); req.resume(); }
          done(false);
        }
      });
      if (canReplay) outbound.end();
      else req.pipe(outbound);
    });
  }

  _scheduleScan(group, reason, force = false) {
    if (this.closed || !templateValid(group.template, this.timing)) return;
    if (!force && group.lastScanDepot === group.latestDepot && group.lastScanHost === group.template.host && Date.now() - group.lastScanAt < this.timing.scanIntervalMs) return;
    if (this.scanning?.group === group && this.scanning.depot === group.latestDepot && this.scanning.host === group.template.host) return;
    this.queue.set(group.id, { group, reason, depot: group.latestDepot, host: group.template.host });
    if (!this.scanning) queueMicrotask(() => this._processQueue());
  }

  async _probe(group, entry, template) {
    return new Promise(resolve => {
      const start = performance.now();
      const headers = cleanHeaders(template.headers);
      headers.host = template.headers.host || template.host;
      headers.range = probeRange(headers.range, this.timing.probeMaxBytes);
      delete headers['content-length'];
      headers['accept-encoding'] = 'identity';
      let settled = false, bytes = 0, response = null, status = null, binary = false;
      const finish = (ok, reason) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        this.sockets.delete(outbound);
        const elapsedMs = performance.now() - start;
        resolve({ ok, reason, status, bytes, elapsedMs, bps: ok ? bytes * SECOND / elapsedMs : null });
      };
      const outbound = http.request({ hostname: entry.ip, port: entry.port, method: 'GET', path: template.path, headers, agent: false });
      this.sockets.add(outbound);
      const timer = setTimeout(() => {
        finish([200, 206].includes(status) && binary && bytes >= this.timing.minimumProbeBytes, bytes >= this.timing.minimumProbeBytes ? 'sampled' : 'timeout');
        outbound.destroy(); response?.destroy();
      }, this.timing.probeTimeoutMs);
      outbound.once('response', upstream => {
        response = upstream;
        status = upstream.statusCode;
        binary = binaryResponse(upstream);
        if (![200, 206].includes(status) || !binary) {
          finish(false, status >= 300 && status < 400 ? 'redirect' : !binary ? 'not_game_content' : `http_${status}`);
          upstream.destroy(); outbound.destroy(); return;
        }
        upstream.on('data', chunk => {
          bytes += chunk.length;
          this._recordBytes(group, entry, chunk.length, true);
          if (bytes >= this.timing.probeMaxBytes) {
            finish(bytes >= this.timing.minimumProbeBytes, 'sampled');
            upstream.destroy(); outbound.destroy();
          }
        });
        upstream.once('end', () => finish(bytes >= this.timing.minimumProbeBytes, bytes >= this.timing.minimumProbeBytes ? 'sampled' : 'insufficient_sample'));
        upstream.once('error', () => finish(false, 'interrupted'));
      });
      outbound.once('error', error => finish(false, safeError(error)));
      outbound.end();
    });
  }

  async _processQueue() {
    if (this.closed || this.scanning || !this.queue.size) return;
    const [key, job] = this.queue.entries().next().value;
    this.queue.delete(key);
    const { group } = job;
    if (!templateValid(group.template, this.timing) || !this._isActive(group)) return;
    const template = { ...group.template, headers: { ...group.template.headers } };
    this.scanning = { group, depot: template.depot, host: template.host, startedAt: Date.now(), completed: 0, total: 0 };
    group.scanStatus = 'testing';
    group.lastScanAt = Date.now();
    group.lastScanDepot = template.depot;
    group.lastScanHost = template.host;
    this._event('test_started', group, { reason: job.reason });
    try {
      await this._ensurePool(group);
      const current = this._pool(group).sort((a, b) => (b.lastSuccessAt > 0 ? 1 : 0) - (a.lastSuccessAt > 0 ? 1 : 0) || (b.scoreBps || 0) - (a.scoreBps || 0));
      const rotate = current.length ? group.scanCursor % current.length : 0;
      const entries = [...current.slice(rotate), ...current.slice(0, rotate)].slice(0, this.timing.maxScanCandidates);
      group.scanCursor += entries.length;
      this.scanning.total = entries.length;
      const startBytes = this.probeBytes;
      for (const entry of entries) {
        if (this.closed || !templateValid(template, this.timing) || !this._isActive(group) || group.latestDepot !== template.depot || group.template?.host !== template.host) break;
        if (this.probeBytes - startBytes >= this.timing.maxScanBytes || this.budgetBytes >= this.timing.probeBudgetBytes) { group.scanStatus = 'traffic_budget_wait'; break; }
        const result = await this._probe(group, entry, template);
        entry.lastProbeAt = Date.now();
        entry.lastStatus = result.status;
        if (result.ok) {
          this._success(group, entry, { ...result, host: template.host, depot: template.depot, probe: true });
        } else this._failure(group, entry, result.reason, result.status);
        this.scanning.completed++;
        this._event('tested_ip', group, { ip: entry.ip, result: result.ok ? 'direct' : result.reason, bps: result.bps, status: result.status });
      }
      if (group.scanStatus === 'testing') group.scanStatus = this._preferred(group, template.host).length ? 'optimized' : 'no_verified_direct_edge';
      this._event('test_finished', group, { tested: this.scanning.completed, candidates: this.scanning.total, state: group.scanStatus });
      if (!this.closed) this._persist();
    } catch (error) {
      group.scanStatus = 'retry_wait';
      this._event('test_retry', group, { reason: safeError(error) });
    } finally {
      this.scanning = null;
      if (this.queue.size && !this.closed) setImmediate(() => this._processQueue());
    }
  }

  _maintenance() {
    if (this.closed) return;
    const now = Date.now();
    if (now - this.budgetAt >= this.timing.probeBudgetWindowMs) { this.budgetAt = now; this.budgetBytes = 0; }
    for (const group of this.groups.values()) {
      if (group.template && !templateValid(group.template, this.timing)) group.template = null;
      if (!group.lastRequestAt || !this._isActive(group)) continue;
      if (now - group.dnsAt >= this.timing.dnsTtlMs) this._ensurePool(group).catch(() => {});
      const best = group.template ? this._preferred(group, group.template.host)[0] : null;
      const slow = best && group.bytes >= MiB && this._rate(group.rateBuckets) < best.scoreBps * 0.25;
      const due = now - group.lastScanAt >= this.timing.scanIntervalMs;
      if (due || (slow && now - group.lastScanAt > Math.min(60000, this.timing.scanIntervalMs))) this._scheduleScan(group, slow ? 'slow_download' : 'periodic');
    }
    for (const transfer of this.transfers) {
      const elapsedMs = performance.now() - transfer.start;
      if (!transfer.canSwitch || elapsedMs < 15000 || transfer.bytes < this.timing.minimumProbeBytes || now - transfer.group.lastSwitchAt < 30000) continue;
      const best = this._preferred(transfer.group, transfer.host)[0];
      if (best && best !== transfer.entry && best.scoreBps >= 512 * 1024 && best.scoreBps > transfer.bytes * SECOND / elapsedMs * 4) {
        transfer.group.lastSwitchAt = now;
        this._event('slow_transfer_retry', transfer.group, { ip: transfer.entry.ip, fasterIP: best.ip });
        // Steam retries/resumes its chunk; never splice two files into a response.
        transfer.outbound.destroy(Object.assign(new Error('Faster edge available'), { code: 'EFASTSWITCH' }));
      }
    }
    if (!this.scanning && this.queue.size) this._processQueue();
    if (now - this.lastPersist >= this.timing.persistIntervalMs) this._persist();
  }

  async refresh(groupId = null) {
    const groups = groupId ? [this.groups.get(groupId)].filter(Boolean) : [...this.groups.values()];
    await Promise.all(groups.map(async group => {
      await this._ensurePool(group, true);
      this._scheduleScan(group, 'manual_check', true);
    }));
    return this.snapshot();
  }

  snapshot() {
    return {
      version: 1, startedAt: new Date(this.startedAt).toISOString(),
      totalBytes: this.totalBytes, rateBps: this._rate(this.rateBuckets), probeBytes: this.probeBytes,
      probeBudgetRemaining: Math.max(0, this.timing.probeBudgetBytes - this.budgetBytes),
      testing: this.scanning ? { group: this.scanning.group.id, name: this.scanning.group.name, completed: this.scanning.completed, total: this.scanning.total } : null,
      groups: [...this.groups.values()].map(group => {
        const selected = new Set(group.template ? this._selectionPool(group, group.template.host) : []);
        return { id: group.id, name: group.name, bytes: group.bytes,
        rateBps: this._rate(group.rateBuckets), active: this._isActive(group),
        scanStatus: group.scanStatus, lastDnsAt: group.dnsAt || null, lastScanAt: group.lastScanAt || null,
        hasRequestSample: templateValid(group.template, this.timing),
        ips: this._pool(group).map(entry => ({ ip: entry.ip, port: entry.port, bytes: entry.bytes,
          rateBps: this._rate(entry.rateBuckets), scoreBps: entry.lastResult === 'direct' ? entry.scoreBps : null,
          result: entry.lastResult, status: entry.lastStatus, active: entry.active,
          lastSuccessAt: entry.lastSuccessAt || null, lastProbeAt: entry.lastProbeAt || null,
          preferred: selected.has(entry),
          historical: entry.lastDnsAt !== group.dnsAt })) };
      }),
      events: this.events.slice(-30),
    };
  }

  _loadState() {
    if (!this.options.statePath) return;
    try {
      const saved = JSON.parse(fs.readFileSync(this.options.statePath, 'utf8').replace(/^\uFEFF/, ''));
      for (const groupRecord of saved.groups || []) {
        const group = this.groups.get(groupRecord.id);
        if (!group) continue;
        for (const record of groupRecord.ips || []) {
          if (!net.isIP(record.ip) || (!this.options.allowTestLocal && !isPublicIP(record.ip)) || Date.now() - record.lastSuccessAt > this.timing.historyTtlMs) continue;
          const entry = this._entry(group, { ip: record.ip, port: Number(record.port || group.target.port) });
          entry.lastSuccessAt = Number(record.lastSuccessAt);
          entry.scoreBps = Number(record.scoreBps) > 0 ? Number(record.scoreBps) : null;
          entry.loaded = true;
          entry.lastResult = 'needs_verification';
        }
      }
    } catch { /* A missing/corrupt speed cache never prevents forwarding. */ }
  }

  _persist() {
    this.lastPersist = Date.now();
    if (!this.options.statePath) return;
    const state = { version: 1, at: new Date().toISOString(), groups: [...this.groups.values()].map(group => ({
      id: group.id, ips: this._pool(group).filter(entry => entry.lastSuccessAt > Date.now() - this.timing.historyTtlMs)
        .map(entry => ({ ip: entry.ip, port: entry.port, lastSuccessAt: entry.lastSuccessAt, scoreBps: entry.scoreBps })),
    })) };
    try {
      fs.mkdirSync(path.dirname(this.options.statePath), { recursive: true });
      fs.writeFileSync(`${this.options.statePath}.tmp`, JSON.stringify(state));
      fs.renameSync(`${this.options.statePath}.tmp`, this.options.statePath);
    } catch { this._event('speed_cache_retry'); }
  }

  async close() {
    this.closed = true;
    clearInterval(this.timer);
    this.queue.clear();
    this.dns.cancel();
    for (const request of this.sockets) request.destroy();
    for (const transfer of this.transfers) if (!transfer.res.destroyed) transfer.res.destroy();
    this.agent.destroy();
    this._persist();
    for (const group of this.groups.values()) group.template = null;
  }
}
