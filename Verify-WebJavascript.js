const fs = require('fs');
const vm = require('vm');
let html = fs.readFileSync(process.argv[2], 'utf8');
if (/\.cs$/i.test(process.argv[2])) {
  const embedded = html.match(/string html = @"([\s\S]*?)";\s*html = html\.Replace/);
  if (!embedded) throw new Error('Could not locate embedded WebAdmin page');
  html = embedded[1].replace(/""/g, '"');
}
const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/gi)];
if (!scripts.length) throw new Error('No Web console script found');
for (const [index, match] of scripts.entries()) new vm.Script(match[1], {filename: `web-console-${index}.js`});
console.log('PASS: served Web console JavaScript syntax');
