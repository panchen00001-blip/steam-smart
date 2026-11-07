import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import crypto from 'node:crypto';

const BEGIN = '# BEGIN SteamAutoOptimizer';
const END = '# END SteamAutoOptimizer';
const OWNER = 'SteamAutoOptimizer';
const HASH = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const canonical = value => String(value).trim().toLowerCase().replace(/\.$/, '');
const isLoopback = ip => /^127\./.test(ip) || ip === '::1';

function decode(bytes) {
  let prefix = Buffer.alloc(0), encoding = 'bytes', payload = bytes;
  if (bytes[0] === 0xff && bytes[1] === 0xfe) {
    prefix = bytes.subarray(0, 2); payload = bytes.subarray(2); encoding = 'utf16le';
  } else if (bytes[0] === 0xfe && bytes[1] === 0xff) {
    prefix = bytes.subarray(0, 2); payload = bytes.subarray(2); encoding = 'utf16be';
  } else if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    prefix = bytes.subarray(0, 3); payload = bytes.subarray(3);
  }
  if (encoding.startsWith('utf16') && payload.length % 2) throw new Error('The hosts file has an incomplete UTF-16 character; no changes were made.');
  if (encoding === 'utf16be') { payload = Buffer.from(payload); payload.swap16(); }
  const text = payload.toString(encoding === 'bytes' ? 'latin1' : 'utf16le');
  return {
    text, encoding, newline: text.match(/\r\n|\n|\r/)?.[0] || '\r\n',
    encode(next) {
      const result = Buffer.from(next, encoding === 'bytes' ? 'latin1' : 'utf16le');
      if (encoding === 'utf16be') result.swap16();
      return Buffer.concat([prefix, result]);
    },
  };
}

function lines(text) {
  const result = [];
  const expression = /([^\r\n]*)(\r\n|\n|\r|$)/g;
  for (const match of text.matchAll(expression)) {
    if (!match[0]) continue;
    result.push({ body: match[1], ending: match[2], raw: match[0] });
  }
  return result;
}

function parseLine(line) {
  const commentAt = line.body.indexOf('#');
  const source = commentAt < 0 ? line.body : line.body.slice(0, commentAt);
  const tokens = [...source.matchAll(/\S+/g)].map(match => ({ value: match[0], start: match.index, end: match.index + match[0].length }));
  if (tokens.length < 2 || !net.isIP(tokens[0].value)) return null;
  return { ip: tokens[0].value, tokens: tokens.slice(1), comment: commentAt < 0 ? '' : line.body.slice(commentAt) };
}

function ownedBlocks(text, domains, ip) {
  const entries = lines(text), inside = new Set(), conflicts = [];
  let open = -1;
  for (let index = 0; index < entries.length; index++) {
    const label = entries[index].body.trim();
    if (label === BEGIN) {
      if (open >= 0) conflicts.push({ reason: 'NestedOwnedBlock' });
      else open = index;
    } else if (label === END) {
      if (open < 0) { conflicts.push({ reason: 'UnmatchedOwnedBlockEnd' }); continue; }
      for (let at = open; at <= index; at++) inside.add(at);
      for (let at = open + 1; at < index; at++) {
        if (!entries[at].body.trim()) continue;
        const parsed = parseLine(entries[at]);
        if (!parsed || parsed.ip !== ip || parsed.tokens.some(token => !domains.has(canonical(token.value)))) {
          conflicts.push({ reason: 'ModifiedOwnedBlock' });
        }
      }
      open = -1;
    }
  }
  if (open >= 0) conflicts.push({ reason: 'UnclosedOwnedBlock' });
  return { entries, inside, conflicts, present: inside.size > 0 };
}

function mappings(entries, domains, excluded = new Set()) {
  const result = [];
  entries.forEach((line, index) => {
    if (excluded.has(index)) return;
    const parsed = parseLine(line);
    if (!parsed) return;
    for (const token of parsed.tokens) {
      const domain = canonical(token.value);
      if (domains.has(domain)) result.push({ domain, ip: parsed.ip, helper: /(?:^|\s)#?UsbEAm_DL_Helper(?:\s|$)/.test(parsed.comment), ownedMarker: parsed.comment.includes(`#${OWNER}`), index });
    }
  });
  return result;
}

function stripDomains(entries, domains, excluded = new Set()) {
  const result = [], records = [];
  entries.forEach((line, index) => {
    if (excluded.has(index)) return;
    const parsed = parseLine(line);
    if (!parsed) { result.push(line.raw); return; }
    const selected = parsed.tokens.filter(token => domains.has(canonical(token.value)));
    if (!selected.length) { result.push(line.raw); return; }
    const remaining = parsed.tokens.filter(token => !domains.has(canonical(token.value)));
    let reduced = null;
    if (remaining.length) {
      let body = line.body;
      for (const token of selected.toReversed()) body = body.slice(0, token.start) + body.slice(token.end);
      reduced = body + line.ending;
      result.push(reduced);
    } else if (parsed.comment && !/(?:UsbEAm_DL_Helper|#SteamAutoOptimizer)/.test(parsed.comment)) {
      reduced = (line.body.match(/^\s*/)?.[0] || '') + parsed.comment + line.ending;
      result.push(reduced);
    }
    records.push({ index, raw: line.raw, reduced, ip: parsed.ip, domains: selected.map(token => canonical(token.value)), tokens: selected.map(token => token.value), ending: line.ending });
  });
  return { text: result.join(''), records };
}

function findUnique(text, fragment) {
  const first = text.indexOf(fragment);
  return first >= 0 && text.indexOf(fragment, first + fragment.length) < 0 ? first : -1;
}

function stateError(message, code) {
  const error = new Error(message); error.code = code; return error;
}

/**
 * Owns only explicitly configured Steam hostnames. No DNS flush, app control,
 * network calls, or logging. Backups retain exact original bytes. Result objects
 * never contain the hosts file body, comments, or journal restoration records.
 */
export class HostsManager {
  constructor({ path: hostsPath, ip = '127.0.0.22', domains, statePath }) {
    if (!hostsPath || !statePath || !Array.isArray(domains) || !domains.length) throw new TypeError('path, statePath and nonempty domains are required.');
    if (!net.isIP(ip) || !isLoopback(ip)) throw new TypeError('The listener address must be a loopback IP.');
    this.path = path.resolve(hostsPath);
    this.statePath = path.resolve(statePath);
    if (this.path.toLowerCase() === this.statePath.toLowerCase()) throw new TypeError('The recovery journal must be separate from the hosts file.');
    this.ip = ip;
    this.domains = [...new Set(domains.map(canonical))].sort();
    if (this.domains.some(domain => !/^(?=.{1,253}$)[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/.test(domain) || !domain.includes('.') || domain.includes('..') || net.isIP(domain))) {
      throw new TypeError('Domains must be exact ASCII DNS hostnames, without ports or wildcards.');
    }
    this.domainSet = new Set(this.domains);
    this.state = null;
    this.queue = Promise.resolve();
  }

  #serialize(operation) {
    const pending = this.queue.then(operation, operation);
    this.queue = pending.catch(() => {});
    return pending;
  }

  async #load() {
    if (this.state) return this.state;
    let state;
    try { state = JSON.parse(await fs.readFile(this.statePath, 'utf8')); }
    catch (error) { if (error.code === 'ENOENT') return null; throw stateError('The hosts recovery journal cannot be read; no changes were made.', 'HOSTS_JOURNAL_INVALID'); }
    if (!state || state.version !== 1 || state.owner !== OWNER || typeof state.path !== 'string' ||
        path.resolve(state.path).toLowerCase() !== this.path.toLowerCase() || state.ip !== this.ip) {
      throw stateError('The hosts recovery journal belongs to a different configuration; restore it before changing configuration.', 'HOSTS_JOURNAL_MISMATCH');
    }
    const validDomain = domain => typeof domain === 'string' && domain === canonical(domain) &&
      /^(?=.{1,253}$)[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/.test(domain) && domain.includes('.') && !domain.includes('..') && !net.isIP(domain);
    const validDomains = Array.isArray(state.domains) && state.domains.length && state.domains.every(validDomain) &&
      JSON.stringify(state.domains) === JSON.stringify([...new Set(state.domains)].sort());
    const validRecords = validDomains && Array.isArray(state.records) && state.records.every(record =>
      record && Number.isInteger(record.index) && record.index >= 0 && typeof record.raw === 'string' && record.raw.length &&
      (record.reduced === null || typeof record.reduced === 'string') && net.isIP(record.ip) &&
      Array.isArray(record.domains) && record.domains.length && record.domains.every(domain => state.domains.includes(domain)) &&
      Array.isArray(record.tokens) && record.tokens.length === record.domains.length &&
      record.tokens.every((token, index) => typeof token === 'string' && canonical(token) === record.domains[index]) &&
      ['', '\r', '\n', '\r\n'].includes(record.ending));
    if (typeof state.backupPath !== 'string' || !state.backupPath || !validRecords ||
        !['prepared', 'active', 'released'].includes(state.phase) ||
        !/^[a-f0-9]{64}$/.test(state.originalSha256) || !/^[a-f0-9]{64}$/.test(state.strippedInitialSha256)) {
      throw stateError('The hosts recovery journal is invalid; no changes were made.', 'HOSTS_JOURNAL_INVALID');
    }
    if (JSON.stringify(state.domains) !== JSON.stringify(this.domains)) {
      if (state.phase !== 'released' || state.pending != null) {
        throw stateError('The hosts recovery journal belongs to a different configuration; restore it before changing configuration.', 'HOSTS_JOURNAL_MISMATCH');
      }
      // A completed release permits a new domain set. Keep the released journal
      // and its backup intact until acquire writes a new, complete journal. A
      // remaining block or marked mapping means recovery is not complete.
      const parsed = ownedBlocks(decode(await fs.readFile(this.path)).text, new Set(state.domains), this.ip);
      const markedMapping = parsed.entries.some(line => parseLine(line)?.comment.includes(`#${OWNER}`));
      if (parsed.present || parsed.conflicts.length || markedMapping) {
        throw stateError('A previous optimizer mapping remains; restore it before changing configuration.', 'HOSTS_ORPHAN_BLOCK');
      }
    }
    this.state = state;
    return state;
  }

  async #save() {
    await fs.mkdir(path.dirname(this.statePath), { recursive: true });
    const temporary = `${this.statePath}.${process.pid}.${crypto.randomBytes(4).toString('hex')}.tmp`;
    try {
      await fs.writeFile(temporary, JSON.stringify(this.state, null, 2) + '\n', { mode: 0o600, flag: 'wx' });
      await fs.rename(temporary, this.statePath);
    } finally { await fs.unlink(temporary).catch(() => {}); }
  }

  #block(newline) {
    return [BEGIN, ...this.domains.map(domain => `${this.ip} ${domain} #${OWNER}`), END, ''].join(newline);
  }

  #result({ changed = false, conflicts = [], owned = false, ...extra } = {}) {
    return { changed, owned, ip: this.ip, domains: [...this.domains], domainCount: this.domains.length,
      phase: this.state?.phase || 'none', conflicts, backupPath: this.state?.backupPath || null, ...extra };
  }

  #conflicts(parsed) {
    const result = [...parsed.conflicts];
    const originals = new Set((this.state?.records || []).flatMap(record => record.domains.map(domain => `${domain}\0${record.ip}`)));
    for (const mapping of mappings(parsed.entries, this.domainSet, parsed.inside)) {
      if (mapping.helper || (mapping.ownedMarker && mapping.ip === this.ip) || originals.has(`${mapping.domain}\0${mapping.ip}`)) continue;
      result.push({ domain: mapping.domain, ip: mapping.ip, reason: 'ExternalMappingChanged' });
    }
    return result;
  }

  async #write(kind, transform) {
    for (let attempt = 0; attempt < 3; attempt++) {
      const before = await fs.readFile(this.path);
      const document = decode(before);
      const plan = transform(document);
      if (plan.conflicts?.length) return this.#result({ conflicts: plan.conflicts, owned: plan.owned });
      const after = document.encode(plan.text);
      if (before.equals(after)) {
        this.state.phase = kind === 'release' ? 'released' : 'active';
        this.state.pending = null;
        this.state.updatedAt = new Date().toISOString();
        await this.#save();
        return this.#result({ owned: kind !== 'release', restoredDomains: plan.restoredDomains || [] });
      }
      // Journal and a full original backup exist before touching the hosts file.
      this.state.pending = { operation: kind, beforeSha256: HASH(before), afterSha256: HASH(after), at: new Date().toISOString() };
      await this.#save();
      if (HASH(await fs.readFile(this.path)) !== HASH(before)) continue;
      // Preserve the file identity/ACL. Renaming over the Windows hosts file can
      // fail or alter permissions; an in-place write also avoids stale baselines.
      const file = await fs.open(this.path, 'r+');
      try {
        let offset = 0;
        while (offset < after.length) {
          const { bytesWritten } = await file.write(after, offset, after.length - offset, offset);
          if (!bytesWritten) throw stateError('The hosts write did not complete; recovery data was retained.', 'HOSTS_WRITE_INCOMPLETE');
          offset += bytesWritten;
        }
        await file.truncate(after.length);
        await file.sync();
      } finally { await file.close(); }
      if (!(await fs.readFile(this.path)).equals(after)) continue;
      this.state.pending = null;
      this.state.phase = kind === 'release' ? 'released' : 'active';
      this.state.updatedAt = new Date().toISOString();
      await this.#save();
      return this.#result({ changed: true, owned: kind !== 'release', restoredDomains: plan.restoredDomains || [] });
    }
    throw stateError('The hosts file kept changing during the update; recovery data was retained.', 'HOSTS_CONCURRENT_WRITE');
  }

  async acquire() {
    return this.#serialize(async () => {
      const state = await this.#load();
      if (state && state.phase !== 'released') return this.#reconcile();
      const original = await fs.readFile(this.path), document = decode(original);
      const parsed = ownedBlocks(document.text, this.domainSet, this.ip);
      if (parsed.present || parsed.conflicts.length) throw stateError('A previous optimizer block exists without an active matching journal; recovery is required.', 'HOSTS_ORPHAN_BLOCK');
      const stripped = stripDomains(parsed.entries, this.domainSet);
      await fs.mkdir(path.dirname(this.statePath), { recursive: true });
      const backupPath = path.join(path.dirname(this.statePath), `hosts.before.${new Date().toISOString().replace(/[:.]/g, '-')}.${crypto.randomBytes(4).toString('hex')}.bak`);
      await fs.writeFile(backupPath, original, { mode: 0o600, flag: 'wx' });
      this.state = { version: 1, owner: OWNER, path: this.path, ip: this.ip, domains: this.domains, phase: 'prepared',
        acquiredAt: new Date().toISOString(), updatedAt: new Date().toISOString(), backupPath,
        originalSha256: HASH(original), strippedInitialSha256: HASH(document.encode(stripped.text)), records: stripped.records, pending: null };
      await this.#save();
      return this.#reconcile();
    });
  }

  async #reconcile() {
    if (!this.state || this.state.phase === 'released') throw stateError('Acquire hosts ownership before reconciling.', 'HOSTS_NOT_ACQUIRED');
    return this.#write('acquire', document => {
      const parsed = ownedBlocks(document.text, this.domainSet, this.ip), conflicts = this.#conflicts(parsed);
      if (conflicts.length) return { conflicts, owned: parsed.present };
      const stripped = stripDomains(parsed.entries, this.domainSet, parsed.inside);
      return { text: this.#block(document.newline) + stripped.text };
    });
  }

  async reconcile() {
    return this.#serialize(async () => { await this.#load(); return this.#reconcile(); });
  }

  async release({ restoreOriginal = true, restoreLoopback = true } = {}) {
    return this.#serialize(async () => {
      const state = await this.#load();
      if (!state || state.phase === 'released') return this.#result();
      const original = restoreOriginal ? await fs.readFile(state.backupPath) : null;
      if (original && HASH(original) !== state.originalSha256) throw stateError('The original hosts backup failed verification; no changes were made.', 'HOSTS_BACKUP_INVALID');
      return this.#write('release', document => {
        const parsed = ownedBlocks(document.text, this.domainSet, this.ip);
        if (parsed.conflicts.length) return { conflicts: parsed.conflicts, owned: parsed.present };
        const ownMarked = new Set(mappings(parsed.entries, this.domainSet, parsed.inside)
          .filter(mapping => mapping.ip === this.ip && mapping.ownedMarker).map(mapping => mapping.index));
        if (!parsed.inside.size && !ownMarked.size) return { text: document.text, restoredDomains: [] };
        let text = parsed.entries.map((line, index) => {
          if (parsed.inside.has(index)) return '';
          return ownMarked.has(index) ? stripDomains([line], this.domainSet).text : line.raw;
        }).join('');
        const eligible = state.records.filter(record => restoreOriginal && (restoreLoopback || !isLoopback(record.ip)));
        if (restoreOriginal && restoreLoopback && HASH(document.encode(text)) === state.strippedInitialSha256) {
          // No unrelated edits: restore every byte, including BOM and final newline.
          const originalDocument = decode(original);
          if (originalDocument.encoding !== document.encoding) throw stateError('The hosts encoding changed; the original file cannot be restored byte-for-byte.', 'HOSTS_ENCODING_CHANGED');
          return { text: originalDocument.text, restoredDomains: [...new Set(eligible.flatMap(record => record.domains))] };
        }
        const existing = new Set(mappings(lines(text), this.domainSet).map(mapping => mapping.domain));
        const restored = new Set(), additions = [];
        for (const record of eligible) {
          const needed = record.domains.filter(domain => !existing.has(domain));
          if (!needed.length) continue;
          const at = record.reduced && needed.length === record.domains.length ? findUnique(text, record.reduced) : -1;
          if (at >= 0) text = text.slice(0, at) + record.raw + text.slice(at + record.reduced.length);
          else {
            // Missing or externally edited original rows: restore just absent
            // target mappings, without replacing unrelated content/comments.
            for (const domain of needed) additions.push(`${record.ip} ${domain}${document.newline}`);
          }
          for (const domain of needed) { existing.add(domain); restored.add(domain); }
        }
        return { text: additions.join('') + text, restoredDomains: [...restored] };
      });
    });
  }

  async inspect() {
    return this.#serialize(async () => {
      await this.#load();
      const document = decode(await fs.readFile(this.path));
      const parsed = ownedBlocks(document.text, this.domainSet, this.ip);
      const ownMappings = mappings(parsed.entries, this.domainSet)
        .filter(mapping => parsed.inside.has(mapping.index) || (mapping.ownedMarker && mapping.ip === this.ip));
      const covered = new Set(ownMappings.filter(mapping => mapping.ip === this.ip).map(mapping => mapping.domain));
      const conflicts = this.state && this.state.phase !== 'released' ? this.#conflicts(parsed) : [...parsed.conflicts];
      if (!this.state && parsed.present) conflicts.push({ reason: 'OrphanOwnedBlock' });
      return this.#result({ owned: parsed.present && covered.size === this.domains.length && !conflicts.length,
        conflicts, mappedDomains: covered.size, encoding: document.encoding, pendingRecovery: Boolean(this.state?.pending) });
    });
  }
}
