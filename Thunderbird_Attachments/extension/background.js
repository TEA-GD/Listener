'use strict';
let db,flushing=false,serial=Promise.resolve();
const ready=new Promise((resolve,reject)=>{const r=indexedDB.open('listener-attachment-mail',1);r.onupgradeneeded=()=>{r.result.createObjectStore('jobs',{keyPath:'key'}).createIndex('due','due');};r.onsuccess=()=>{db=r.result;resolve();};r.onerror=()=>reject(r.error);});
async function storage(mode,action){await ready;return new Promise((resolve,reject)=>{const tx=db.transaction('jobs',mode);let result;const r=action(tx.objectStore('jobs'));r.onsuccess=()=>result=r.result;tx.oncomplete=()=>resolve(result);tx.onerror=()=>reject(tx.error);tx.onabort=()=>reject(tx.error||new Error('Mail queue transaction aborted'));});}
const put=job=>storage('readwrite',s=>s.put(job));
async function settings(){return (await browser.storage.local.get('settings')).settings||{enabled:false,port:58766,token:''};}
async function enqueue(folder,list,received){const cfg=await settings();if(!cfg.enabled||AttachmentMail.outgoing(folder))return;
 let page=list;do{for(const message of page.messages||[]){if(message.external)continue;
  const digest=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(JSON.stringify([folder.accountId,message.headerMessageId||message.id])));const key=Array.from(new Uint8Array(digest),b=>b.toString(16).padStart(2,'0')).join('');
  if(await storage('readonly',s=>s.get(key)))continue;
  await put({key,alert_id:crypto.randomUUID(),received,sender:message.author||'',recipients:[...new Set([...(message.recipients||[]),...(message.ccList||[]),...(message.bccList||[])])],account:folder.accountId,headerId:message.headerMessageId||'',number:message.id,state:'pending',due:0,attempts:0});
 }page=page.id?await browser.messages.continueList(page.id):null;}while(page);}
async function resolveMessage(job){
 try{const current=await browser.messages.get(job.number);if((!job.headerId||current.headerMessageId===job.headerId)&&current.author===job.sender&&current.folder?.accountId===job.account)return current;}catch{}
 if(job.headerId){let page=await browser.messages.query({accountId:job.account,headerMessageId:job.headerId});do{const match=page.messages.find(m=>m.author===job.sender);if(match){if(page.id)await browser.messages.abortList(page.id);return match;}page=page.id?await browser.messages.continueList(page.id):null;}while(page);}
 throw Error('Message unavailable; restore/download it to retry attachment metadata.');
}
async function flush(){await ready;if(flushing)return;flushing=true;try{
 const jobs=await storage('readonly',s=>s.index('due').getAll(IDBKeyRange.upperBound(Date.now()),20));for(const job of jobs){
  try{const cfg=await settings();if(!cfg.enabled)break;if(cfg.token.length<32)throw Error('Set the local Agent mail token in add-on settings.');
   if(!job.payload){const message=await resolveMessage(job);job.payload=AttachmentMail.payload(job,await browser.messages.listAttachments(message.id));
    if(!job.payload){await put({key:job.key,state:'no_attachments'});continue;}await put(job);}
   const response=await fetch('http://127.0.0.1:'+cfg.port+'/api/mail-attachments',{method:'POST',headers:{Authorization:'Bearer '+cfg.token,'Content-Type':'application/json'},body:JSON.stringify(job.payload),signal:AbortSignal.timeout(10000),redirect:'error',credentials:'omit'});
   if(!response.ok)throw Error('Agent HTTP '+response.status+'; metadata remains queued.');const ack=await response.json();if(ack.status!=='queued'||ack.alert_id!==job.alert_id)throw Error('Agent did not acknowledge local storage.');
   await put({key:job.key,state:'sent'});await browser.storage.local.set({lastError:'',lastDelivered:new Date().toISOString()});
  }catch(error){job.attempts++;job.due=Date.now()+Math.min(3600000,60000*2**Math.min(job.attempts,6));await put(job);await browser.storage.local.set({lastError:error.message});}
 }
 await browser.storage.local.set({pending:await storage('readonly',s=>s.index('due').count())});
}finally{flushing=false;}}
browser.messages.onNewMailReceived.addListener((folder,list)=>{const received=new Date().toISOString();serial=serial.then(()=>enqueue(folder,list,received)).then(flush).catch(e=>browser.storage.local.set({lastError:e.message}));},true);
browser.alarms.create('attachment-mail-retry',{periodInMinutes:1});browser.alarms.onAlarm.addListener(a=>{if(a.name==='attachment-mail-retry')void flush();});browser.storage.onChanged.addListener(changes=>{if(changes.settings)void flush();});browser.browserAction.onClicked.addListener(()=>browser.runtime.openOptionsPage());void flush();
