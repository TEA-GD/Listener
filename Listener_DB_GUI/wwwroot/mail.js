'use strict';
const $=id=>document.getElementById(id);
let page=1,total=0,request=null,detailRequest=null;
function el(tag,text){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;}
function array(value){try{return JSON.parse(value||'[]');}catch{return [value];}}
function error(value){$('error').textContent=value;$('error').hidden=!value;}
async function api(path,signal){const response=await fetch(path,{signal,headers:{Authorization:'Bearer '+$('mailKey').value}});const data=await response.json();if(!response.ok)throw new Error(data.error||'Could not load mail.');return data;}
async function load(){
    if(!$('mailKey').value){$('mailStatus').textContent='Enter the mail viewing key and click Load / search mail.';return;}
    request?.abort();const current=new AbortController();request=current;$('prev').disabled=$('next').disabled=true;
    try{const data=await api('/api/mail?'+new URLSearchParams({page,q:$('search').value}),current.signal);error('');total=Number(data.total);$('mailRows').replaceChildren();
        $('mailStatus').textContent=!data.available?'No mail receiver table yet. Start Listener_MailReceiver with this collector database.':total.toLocaleString()+' matching mail alerts';
        for(const mail of data.alerts){const tr=el('tr');for(const text of [mail.received_utc,mail.client_label,mail.sender,array(mail.recipients_json).join(', '),mail.subject,array(mail.attachment_names_json).join(', ')||'—',mail.body_status])tr.append(el('td',text));const td=el('td'),button=el('button','Read message');button.setAttribute('aria-label','Read message '+mail.subject);button.onclick=()=>open(mail.alert_id);td.append(button);tr.append(td);$('mailRows').append(tr);}
        if(!data.alerts.length){const tr=el('tr'),td=el('td','No external mail alerts to display.');td.colSpan=8;tr.append(td);$('mailRows').append(tr);}
        $('page').textContent=`Page ${page} of ${Math.max(1,Math.ceil(total/50))}`;$('prev').disabled=page===1;$('next').disabled=page*50>=total;
    }catch(e){if(e.name!=='AbortError')error(e.message);}
}
async function open(id){
    detailRequest?.abort();const current=new AbortController();detailRequest=current;$('mailDetail').hidden=true;
    try{const mail=await api('/api/mail/'+encodeURIComponent(id),current.signal);error('');$('mailFields').replaceChildren();
        for(const [label,value] of [['Subject',mail.subject],['From',mail.sender],['From address',mail.sender_address],['Sender domain',mail.sender_domain||'Unparseable sender'],['To',array(mail.recipients_json).join(', ')],['Cc',array(mail.cc_json).join(', ')],['Visible Bcc',array(mail.bcc_json).join(', ')],['Receiving account addresses',array(mail.mailbox_addresses_json).join(', ')],['Delivered-To headers',array(mail.delivered_to_json).join(', ')],['Attachments',array(mail.attachment_names_json).join(', ')],['Message date',mail.message_date_utc],['Listener received',mail.received_utc],['Body status',mail.body_status],['Client',mail.client_label],['Account ID',mail.account_id],['Message-ID',mail.message_id],['Alert ID',mail.alert_id]])$('mailFields').append(el('dt',label),el('dd',value||'—'));
        $('mailBody').textContent=mail.body_text||'(No body text available; see body status.)';$('mailParts').replaceChildren();for(const part of array(mail.text_parts_json)){const title=el('h3',part.content_type+' · '+part.part_name),pre=el('pre',part.text);$('mailParts').append(title,pre);}
        $('mailDetail').hidden=false;$('mailDetail').scrollIntoView({behavior:'smooth'});
    }catch(e){if(e.name!=='AbortError')error(e.message);}
}
$('searchForm').onsubmit=e=>{e.preventDefault();page=1;load();};$('refresh').onclick=load;$('prev').onclick=()=>{if(page>1){page--;load();}};$('next').onclick=()=>{if(page*50<total){page++;load();}};$('closeDetail').onclick=()=>{detailRequest?.abort();$('mailDetail').hidden=true;};load();
