const assert=require('node:assert/strict');const C=require('../Thunderbird_Attachments/extension/core.js');
const job={alert_id:'id',received:'2026-11-23T12:00:00Z',sender:'from@example.test',recipients:['to@example.test']};
assert.equal(C.payload(job,[]),null);
const p=C.payload(job,[{name:'report.pdf',contentType:'application/pdf',size:123},{name:'empty.zip',size:0},{name:'unknown.bin'}]);
assert.deepEqual(Object.keys(p).sort(),['alert_id','attachments','date_received_utc','recipients','sender']);assert.equal(p.date_received_utc,job.received);assert.equal(p.attachments[0].format,'application/pdf');assert.equal(p.attachments[0].size_bytes,123);assert.equal(p.attachments[1].size_bytes,0);assert.equal(p.attachments[2].size_bytes,null);assert(C.outgoing({specialUse:['sent']}));assert(!C.outgoing({specialUse:['inbox']}));
const source=require('node:fs').readFileSync(require('node:path').join(__dirname,'../Thunderbird_Attachments/extension/background.js'),'utf8');
for(const forbidden of ['getFull(', 'getRaw(', 'getAttachmentFile(', 'message.subject'])assert(!source.includes(forbidden));
console.log('PASS: attachment-only filtering, metadata allowlist, observed receipt date, formats, zero/unknown sizes and privacy API guards');
