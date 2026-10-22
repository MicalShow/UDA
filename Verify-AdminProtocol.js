// Guard the protocol boundary between WebAdmin's legacy management API and
// service-owned repository mutation endpoints.
const fs = require('fs');
const source = fs.readFileSync(process.argv[2] || 'WebAdmin.cs', 'utf8');
function requireMatch(pattern, message) {
  if (!pattern.test(source)) throw new Error(message);
}
requireMatch(/const serviceProtoHeader=\{'X-UDA-Protocol':'__UDA_LAN_PROTOCOL__'\}/,
  'service protocol header placeholder missing');
requireMatch(/html\s*=\s*html\.Replace\("__UDA_LAN_PROTOCOL__",\s*LanProtocol\.Version\.ToString\(CultureInfo\.InvariantCulture\)\)/,
  'served page does not inject the current LAN protocol version');
for (const [endpoint, fn] of [
  ['/api/admin/import?src=', 'importRepo'],
  ['/api/admin/organize', 'organizeRepository'],
  ['/api/admin/duplicates', 'auditDuplicates'],
  ['/api/admin/dedupe', 'cleanDuplicates'],
  ['/api/admin/ai/tasks', 'runAiTask'],
  ['/api/admin/drivers/delete', 'deleteSelected'],
]) {
  const match = source.match(new RegExp(`async function ${fn}\\([\\s\\S]*?(?=\\n(?:async )?function |\\nconst protoHeader)`));
  if (!match || !match[0].includes(endpoint) || !match[0].includes('serviceProtoHeader')) {
    throw new Error(`${fn} must call ${endpoint} with serviceProtoHeader`);
  }
}
console.log('PASS: service-owned repository actions send the current LAN protocol version');
