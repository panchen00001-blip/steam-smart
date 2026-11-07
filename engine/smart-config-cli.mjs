import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ConfigError, MAX_ROWS_BYTES, applyDomainRows, configView,
  readConfigFile, readJsonFile, saveDomainRows, safeConfigError,
} from './smart-config-manager.mjs';

function options(argv) {
  const [command, ...rest] = argv;
  const allowed = {
    read: ['--config'], validate: ['--config', '--input'],
    save: ['--config', '--input'], defaults: ['--config', '--defaults'],
  }[command];
  if (!allowed || rest.length % 2) throw new ConfigError('配置命令无效，请重新打开域名设置。');
  const values = {};
  for (let index = 0; index < rest.length; index += 2) {
    const key = rest[index], value = rest[index + 1];
    if (!allowed.includes(key) || Object.hasOwn(values, key) || !value || value.startsWith('--')) {
      throw new ConfigError('配置命令参数无效，请重新打开域名设置。');
    }
    values[key] = value;
  }
  if (allowed.some(key => !values[key])) throw new ConfigError('配置命令缺少必要参数，请重新打开域名设置。');
  return { command, values };
}

export async function runCli(argv) {
  const { command, values } = options(argv);
  if (command === 'defaults') return { ok: true, ...configView(await readConfigFile(values['--defaults'])) };
  if (command === 'read') return { ok: true, ...configView(await readConfigFile(values['--config'])) };
  const rows = await readJsonFile(values['--input'], MAX_ROWS_BYTES);
  if (command === 'validate') {
    return { ok: true, config: applyDomainRows(await readConfigFile(values['--config']), rows) };
  }
  return { ok: true, ...configView(await saveDomainRows(values['--config'], rows)) };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.stdout.write(`${JSON.stringify(await runCli(process.argv.slice(2)))}\n`); }
  catch (error) {
    process.stdout.write(`${JSON.stringify({ ok: false, message: safeConfigError(error) })}\n`);
    process.exitCode = 1;
  }
}
