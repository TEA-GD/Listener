'use strict';
const assert=require('node:assert/strict');
const Core=require('../Thunderbird_Addon_Metadata/extension/core.js');
const message={id:1,author:'From <from@outside.test>',date:'2026-11-18T10:00:00Z',headerMessageId:'test',
    recipients:['to@example.com'],ccList:['cc@example.com','to@example.com'],bccList:['bcc@example.com'],
    subject:'DO_NOT_COLLECT',body:'DO_NOT_COLLECT'};
const metadata=Core.metadata(message,[{name:'Привет.txt'},{name:''}]);
assert.deepEqual(Object.keys(metadata),['message_date_utc','sender','recipients','has_attachments','attachment_names']);
assert.deepEqual(metadata.recipients,['to@example.com','cc@example.com','bcc@example.com']);
assert.equal(metadata.has_attachments,true);
assert.deepEqual(metadata.attachment_names,['Привет.txt','(unnamed attachment)']);
assert.equal(Core.metadata(message,[]).has_attachments,false);
assert(!JSON.stringify(Core.minimalHeader(message)).includes('DO_NOT_COLLECT'));
assert.equal(Core.internal('a@dept.example.com',['example.com'],true),true);
assert.equal(Core.internal('a@example.com.evil.test',['example.com'],true),false);
assert.equal(Core.settings({}).receiverUrl,'http://127.0.0.1:6517');
console.log('Metadata extraction, minimal headers and filtering passed.');
