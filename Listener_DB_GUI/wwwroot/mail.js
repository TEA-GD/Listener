'use strict';
const $=id=>document.getElementById(id);
let page=1,total=0,request=null,detailRequest=null;
const metadataMode=()=>$('mailMode').value==='metadata';
function el(tag,text){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;}
function array(value){try{return JSON.parse(value||'[]');}catch{return [value];}}
function error(value){$('error').textContent=value;$('error').hidden=!value;}
async function api(path,signal){const response=await fetch(path,{signal,headers:{Authorization:'Bearer '+$('mailKey').value}});const data=await response.json();if(!response.ok)throw new Error(data.error||'Could not load mail.');return data;}
async function load(){
    if(!$('mailKey').value){$('mailStatus').textContent='Enter the mail viewing key and click Load / search mail.';return;}
    request?.abort();const current=new AbortController();request=current;$('prev').disabled=$('next').disabled=true;
    try{const data=await api('/api/mail?'+new URLSearchParams({mode:$('mailMode').value,page,q:$('search').value}),current.signal);error('');total=Number(data.total);$('mailRows').replaceChildren();
        $('mailStatus').textContent=!data.available?'No mail receiver table yet. Start the updated Collector or matching mail receiver with this database.':total.toLocaleString()+' matching mail alerts';
        for(const mail of data.alerts){const tr=el('tr');for(const text of [metadataMode()?mail.message_date_utc:mail.received_utc,mail.client_label,mail.sender,array(mail.recipients_json).join(', '),metadataMode()?(mail.has_attachments?'yes':'no'):mail.subject,array(mail.attachment_names_json).join(', ')||'—',metadataMode()?'Metadata only':mail.body_status])tr.append(el('td',text));const td=el('td'),button=el('button',metadataMode()?'View metadata':'Read message');button.setAttribute('aria-label',metadataMode()?'View metadata '+mail.sender:'Read message '+mail.subject);button.onclick=()=>open(mail.alert_id);td.append(button);tr.append(td);$('mailRows').append(tr);}
        if(!data.alerts.length){const tr=el('tr'),td=el('td','No external mail alerts to display.');td.colSpan=8;tr.append(td);$('mailRows').append(tr);}
        $('page').textContent=`Page ${page} of ${Math.max(1,Math.ceil(total/50))}`;$('prev').disabled=page===1;$('next').disabled=page*50>=total;
    }catch(e){if(e.name!=='AbortError')error(e.message);}
}
async function open(id){
    detailRequest?.abort();const current=new AbortController();detailRequest=current;$('mailDetail').hidden=true;
    try{const mail=await api('/api/mail/'+encodeURIComponent(id)+'?mode='+$('mailMode').value,current.signal);error('');$('mailFields').replaceChildren();
        const fields=metadataMode()?[['Date',mail.message_date_utc],['Sender',mail.sender],['Receiver (To/Cc/visible Bcc)',array(mail.recipients_json).join(', ')],['Has attachments',mail.has_attachments?'yes':'no'],['Attachment filenames',array(mail.attachment_names_json).join(', ')],['Client',mail.client_label],['Listener received',mail.received_utc]]:[['Subject',mail.subject],['From',mail.sender],['From address',mail.sender_address],['Sender domain',mail.sender_domain||'Unparseable sender'],['To',array(mail.recipients_json).join(', ')],['Cc',array(mail.cc_json).join(', ')],['Visible Bcc',array(mail.bcc_json).join(', ')],['Receiving account addresses',array(mail.mailbox_addresses_json).join(', ')],['Delivered-To headers',array(mail.delivered_to_json).join(', ')],['Attachments',array(mail.attachment_names_json).join(', ')],['Message date',mail.message_date_utc],['Listener received',mail.received_utc],['Body status',mail.body_status],['Client',mail.client_label],['Account ID',mail.account_id],['Message-ID',mail.message_id],['Alert ID',mail.alert_id]];
        for(const [label,value] of fields)$('mailFields').append(el('dt',label),el('dd',value||'—'));
        $('mailTextSection').hidden=metadataMode();
        $('mailBody').textContent=metadataMode()?'':mail.body_text||'(No body text available; see body status.)';$('mailParts').replaceChildren();for(const part of metadataMode()?[]:array(mail.text_parts_json)){const title=el('h3',part.content_type+' · '+part.part_name),pre=el('pre',part.text);$('mailParts').append(title,pre);}
        $('mailDetail').hidden=false;$('mailDetail').scrollIntoView({behavior:'smooth'});
    }catch(e){if(e.name!=='AbortError')error(e.message);}
}
$('searchForm').onsubmit=e=>{e.preventDefault();page=1;load();};$('refresh').onclick=load;$('prev').onclick=()=>{if(page>1){page--;load();}};$('next').onclick=()=>{if(page*50<total){page++;load();}};$('closeDetail').onclick=()=>{detailRequest?.abort();$('mailDetail').hidden=true;};load();

$('mailMode').onchange=()=>{request?.abort();detailRequest?.abort();page=1;total=0;$('mailRows').replaceChildren();$('mailDetail').hidden=true;$('mailFields').replaceChildren();$('mailBody').textContent='';$('mailParts').replaceChildren();$('dateHeading').textContent=metadataMode()?'Message date (UTC)':'Received by Listener (UTC)';$('subjectHeading').textContent=metadataMode()?'Has attachments':'Subject';$('statusHeading').textContent=metadataMode()?'Collection mode':'Body status';load();};
