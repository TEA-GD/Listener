'use strict';
const Core=ListenerMail;
let database,flushing=false,captureSerial=Promise.resolve(),activeRequest=null;
const ready=(async()=>{
    const saved=await browser.storage.local.get(['clientId']);
    if(!saved.clientId)await browser.storage.local.set({clientId:crypto.randomUUID()});
    database=await new Promise((resolve,reject)=>{const r=indexedDB.open('listener-mail-alerts',1);r.onupgradeneeded=()=>{const s=r.result.createObjectStore('jobs',{keyPath:'key'});s.createIndex('due','next_attempt');s.createIndex('error','error');};r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(r.error);});
})();
async function store(mode,operation){await ready;return new Promise((resolve,reject)=>{const t=database.transaction('jobs',mode);let result;try{const r=operation(t.objectStore('jobs'));r.onsuccess=()=>result=r.result;}catch(e){t.abort();reject(e);return;}t.oncomplete=()=>resolve(result);t.onerror=()=>reject(t.error);t.onabort=()=>reject(t.error||new Error('Queue transaction aborted'));});}
const get=key=>store('readonly',s=>s.get(key));const put=job=>store('readwrite',s=>s.put(job));
async function config(){return Core.settings((await browser.storage.local.get('settings')).settings||{});}
async function status(error=''){const failed=await store('readonly',s=>s.index('error').openKeyCursor());await browser.storage.local.set({lastError:error||failed?.key||(await browser.storage.local.get('lastError')).lastError||'',lastActivity:new Date().toISOString(),pending:await store('readonly',s=>s.index('due').count())});}
async function senderAddress(value){if(typeof browser.messengerUtilities?.parseMailboxString!=='function')throw new Error('Thunderbird mailbox parser API is unavailable. Collection is paused; use a supported Thunderbird version.');try{const mailboxes=await browser.messengerUtilities.parseMailboxString(value||'');return mailboxes.length===1?mailboxes[0].email||'':'';}catch{return '';}}
function htmlText(html){
    // A disconnected template is inert: no scripts execute and remote images are not loaded.
    const template=document.createElement('template');template.innerHTML=html;
    template.content.querySelectorAll('script,style,head').forEach(n=>n.remove());
    template.content.querySelectorAll('br,p,div,li,tr,h1,h2,h3,blockquote').forEach(n=>n.append(document.createTextNode('\n')));
    return template.content.textContent;
}
async function enqueue(folder,list){
    const c=await config();if(!c.enabled||Core.outgoing(folder)||!c.accountIds.includes(folder.accountId))return;
    let page=list;
    do{
        for(const message of page.messages||[]){
            if(message.external)continue;
            const address=await senderAddress(message.author);
            if(Core.internal(address,c.internalDomains,c.includeSubdomains))continue;
            const identity=JSON.stringify([folder.accountId,message.headerMessageId||'id:'+message.id,new Date(message.date).toISOString(),message.author,message.recipients||[],message.ccList||[]]);
            const digest=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(identity));
            const key=Array.from(new Uint8Array(digest),b=>b.toString(16).padStart(2,'0')).join('');
            if(await get(key))continue;
            await put({key,account_id:folder.accountId,header:JSON.parse(JSON.stringify(message)),message_number:message.id,alert_id:crypto.randomUUID(),attempts:0,next_attempt:Date.now(),state:'pending'});
        }
        page=page.id?await browser.messages.continueList(page.id):null;
    }while(page);
    await status();
}
async function resolveMessage(job){
    try{
        const current=await browser.messages.get(job.message_number);
        if(current.author===job.header.author&&new Date(current.date).toISOString()===new Date(job.header.date).toISOString()&&(!job.header.headerMessageId||current.headerMessageId===job.header.headerMessageId)&&(!current.folder||current.folder.accountId===job.account_id))return current;
    }catch{}
    if(job.header.headerMessageId){
        let page=await browser.messages.query({accountId:job.account_id,headerMessageId:job.header.headerMessageId});
        do{const current=page.messages.find(m=>m.author===job.header.author&&new Date(m.date).toISOString()===new Date(job.header.date).toISOString());if(current){if(page.id)await browser.messages.abortList(page.id);return current;}page=page.id?await browser.messages.continueList(page.id):null;}while(page);
    }
    throw new Error('Queued mail is no longer readable. Restore/download it in Thunderbird and retry.');
}
async function capture(job,c){
    const message=await resolveMessage(job);
    const full=await browser.messages.getFull(message.id);
    let attachments;
    try{attachments=await browser.messages.listAttachments(message.id);}catch(e){if(['fail','skipped'].includes(full.decryptionStatus))attachments=[];else throw e;}
    const body=Core.mimeBody(full,attachments,htmlText);
    const account=await browser.accounts.get(job.account_id,false);
    const headers=full.headers||{};
    return {alert_id:job.alert_id,client_id:(await browser.storage.local.get('clientId')).clientId,client_label:c.clientLabel,
        account_id:job.account_id,message_key:job.key,message_id:message.headerMessageId||'',sender:message.author||headers.from?.join(', ')||'',
        recipients:message.recipients?.length?message.recipients:(headers.to||[]),cc:message.ccList?.length?message.ccList:(headers.cc||[]),bcc:message.bccList?.length?message.bccList:(headers.bcc||[]),
        mailbox_addresses:(account.identities||[]).map(i=>i.email).filter(Boolean),delivered_to:[...(headers['delivered-to']||[]),...(headers['x-original-to']||[])],
        subject:message.subject||'',body_text:body.text,text_parts:body.parts,attachment_names:attachments.map(a=>a.name||''),
        body_status:message.headersOnly?'headers_only':full.decryptionStatus==='fail'?'decryption_failed':full.decryptionStatus==='skipped'?'decryption_skipped':body.parts.length?'available':'empty',
        message_date_utc:message.date?new Date(message.date).toISOString():null};
}
async function flush(){
    await ready;if(flushing)return;flushing=true;
    try{
        const keys=await store('readonly',s=>s.index('due').getAllKeys(IDBKeyRange.upperBound(Date.now()),20));
        for(const key of keys){
            const job=await get(key);if(!job||job.state!=='pending')continue;
            try{
                let c=await config();if(!c.enabled)break;
                if(!c.accountIds.includes(job.account_id)){job.next_attempt=Date.now()+60000;await put(job);continue;}
                if(!job.payload)job.payload=await capture(job,c);
                c=await config();if(!c.enabled)break;
                if(!c.accountIds.includes(job.account_id)){job.next_attempt=Date.now()+60000;await put(job);continue;}
                if(Core.internal(await senderAddress(job.payload.sender),c.internalDomains,c.includeSubdomains)){
                    await put({key:job.key,state:'ignored',alert_id:job.alert_id});continue;
                }
                await put(job); // Persist body before transport; mail can move or disappear later.
                const controller=new AbortController();activeRequest=controller;
                const timeout=setTimeout(()=>controller.abort(),15000);
                let response;
                try{response=await fetch(Core.receiver(c.receiverUrl,c.allowHttp).href.replace(/\/$/,'')+'/api/mail-alerts',{method:'POST',headers:{Authorization:'Bearer '+c.receiverToken,'Content-Type':'application/json'},body:JSON.stringify(job.payload),signal:controller.signal,redirect:'error',credentials:'omit'});}finally{clearTimeout(timeout);activeRequest=null;}
                if(!response.ok)throw new Error('Listener returned HTTP '+response.status+'. Mail remains queued.');
                const ack=await response.json();
                if(!['stored','duplicate','ignored_internal'].includes(ack.status)||!ack.alert_id||ack.message_key!==job.key||(ack.status!=='duplicate'&&ack.alert_id!==job.alert_id))throw new Error('Listener did not acknowledge mail storage.');
                await put({key:job.key,state:ack.status==='ignored_internal'?'ignored':'sent',alert_id:job.alert_id,acknowledged_utc:new Date().toISOString()});
                await browser.storage.local.set({lastDelivered:new Date().toISOString(),lastError:''});
            }catch(e){job.attempts++;job.error=e.message;job.next_attempt=Date.now()+Math.min(3600000,60000*2**Math.min(job.attempts-1,6));await put(job);await status(e.message);}
        }
        await status((await browser.storage.local.get('lastError')).lastError||'');
    }catch(e){await browser.storage.local.set({lastError:e.message});}finally{flushing=false;}
}
browser.messages.onNewMailReceived.addListener((folder,list)=>{
    captureSerial=captureSerial.then(()=>enqueue(folder,list)).then(()=>{void flush();}).catch(async e=>{await browser.storage.local.set({lastError:e.message});});
},true);
browser.alarms.onAlarm.addListener(a=>{if(a.name==='listener-mail-retry')flush();});
browser.alarms.create('listener-mail-retry',{periodInMinutes:1});
browser.storage.onChanged.addListener(changes=>{if(changes.settings?.newValue?.enabled===false)activeRequest?.abort();});
browser.browserAction.onClicked.addListener(()=>browser.runtime.openOptionsPage());
browser.runtime.onMessage.addListener(async message=>{
    if(message.type==='retry'){
        await ready;
        await new Promise((resolve,reject)=>{
            const transaction=database.transaction('jobs','readwrite'),request=transaction.objectStore('jobs').openCursor();
            request.onsuccess=()=>{const cursor=request.result;if(!cursor)return;const job=cursor.value;if(job.state==='pending'){job.next_attempt=Date.now();cursor.update(job);}cursor.continue();};
            transaction.oncomplete=resolve;transaction.onerror=()=>reject(transaction.error);transaction.onabort=()=>reject(transaction.error||new Error('Retry update aborted'));
        });
        flush();return {ok:true};
    }
    if(message.type==='status'){await status((await browser.storage.local.get('lastError')).lastError||'');return browser.storage.local.get(['lastError','pending','lastDelivered','lastActivity']);}
    if(message.type==='test'){
        const c=await config();if(!c.enabled)throw new Error('Enable and save the configured accounts/receiver first.');
        const key='test-'+crypto.randomUUID(),id=crypto.randomUUID();
        await put({key,account_id:c.accountIds[0],alert_id:id,attempts:0,next_attempt:Date.now(),state:'pending',payload:{alert_id:id,client_id:(await browser.storage.local.get('clientId')).clientId,client_label:c.clientLabel,account_id:c.accountIds[0],message_key:key,message_id:'',sender:'Listener Test <test@listener-test.invalid>',recipients:['recipient@listener-test.invalid'],cc:[],bcc:[],mailbox_addresses:[],delivered_to:[],subject:'Listener synthetic mail test',body_text:'Synthetic test. No real email body was read.',text_parts:[{content_type:'text/plain',text:'Synthetic test. No real email body was read.',part_name:'test'}],attachment_names:['sample.txt'],body_status:'available',message_date_utc:new Date().toISOString()}});flush();return {ok:true};
    }
});
ready.then(flush).catch(e=>browser.storage.local.set({lastError:e.message}));
