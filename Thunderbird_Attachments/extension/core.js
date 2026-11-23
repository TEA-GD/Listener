(function(root){'use strict';
function outgoing(folder){return [folder?.type,...(folder?.specialUse||[])].some(t=>['sent','drafts','templates','outbox'].includes(t));}
function payload(job,attachments){
 if(!attachments.length)return null;
 return {alert_id:job.alert_id,date_received_utc:job.received,sender:job.sender,recipients:job.recipients,
 attachments:attachments.map(a=>({name:a.name||'(unnamed)',format:a.contentType||((a.name||'').includes('.')?a.name.split('.').pop().toLowerCase():'unknown'),size_bytes:Number.isSafeInteger(a.size)&&a.size>=0?a.size:null}))};
}
const api={outgoing,payload};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.AttachmentMail=api;
})(globalThis);
