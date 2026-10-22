const fs = require('fs');
const first = fs.statSync(process.argv[2], {bigint:true});
const second = fs.statSync(process.argv[3], {bigint:true});
if (first.ino !== second.ino || first.nlink < 2n || second.nlink < 2n) throw new Error('Unchanged file occupies a separate copy instead of a shared NTFS file');
console.log('PASS: unchanged binary versions share the same NTFS file identity and disk allocation');
