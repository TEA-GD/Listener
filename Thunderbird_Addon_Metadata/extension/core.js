/* Metadata-only helpers. No message body extraction. */
(function(root){
'use strict';
function normalizeDomain(value){
    const text=String(value).trim().replace(/\.$/,'').toLowerCase();
    if(!text||/[\s/@:#?\\]/.test(text))throw new Error('Enter domain names without @, URLs or paths.');
    const hostname=new URL('https://'+text).hostname;
    if(!hostname||hostname!==hostname.toLowerCase()||hostname.split('.').some(label=>!label||label.length>63||!/^([a-z0-9]|[a-z0-9][a-z0-9-]*[a-z0-9])$/.test(label)))throw new Error('Invalid internal domain.');
    return hostname;
}
function domains(text){const values=Array.isArray(text)?text:String(text).split(/[\s,;]+/);return [...new Set(values.filter(Boolean).map(normalizeDomain))];}
function internal(address,allowed,includeSubdomains){
    const at=String(address).lastIndexOf('@');if(at<0)return false;
    let domain;try{domain=normalizeDomain(address.slice(at+1));}catch{return false;}
    return allowed.some(d=>domain===d||(includeSubdomains&&domain.endsWith('.'+d)));
}
function receiver(value,allowHttp){
    const url=new URL(value);
    if(!['https:','http:'].includes(url.protocol)||url.username||url.password||url.search||url.hash)throw new Error('Use an HTTP(S) receiver URL without credentials, query or fragment.');
    if(url.protocol==='http:'&&!['localhost','127.0.0.1','[::1]'].includes(url.hostname)&&!allowHttp)throw new Error('Use HTTPS for a remote receiver, or explicitly allow unencrypted HTTP.');
    url.pathname=url.pathname.replace(/\/+$/,'');return url;
}
function permissionPattern(value){const url=new URL(value);return url.protocol+'//'+url.hostname+'/*';}
function outgoing(folder){return [folder?.type,...(folder?.specialUse||[])].some(t=>['sent','drafts','templates','outbox'].includes(t));}
function minimalHeader(message){
    return {id:message.id,author:message.author||'',date:new Date(message.date).toISOString(),
        headerMessageId:message.headerMessageId||'',recipients:message.recipients||[],
        ccList:message.ccList||[],bccList:message.bccList||[]};
}
function metadata(message,attachments){
    return {message_date_utc:new Date(message.date).toISOString(),sender:message.author||'',
        recipients:[...new Set([...(message.recipients||[]),...(message.ccList||[]),...(message.bccList||[])])],
        has_attachments:attachments.length>0,attachment_names:attachments.map(a=>a.name||'(unnamed attachment)')};
}
function settings(value){
    const c={enabled:false,internalDomains:[],includeSubdomains:true,accountIds:[],receiverUrl:'http://127.0.0.1:6517',receiverToken:'',clientLabel:'',allowHttp:false,...value};
    c.internalDomains=domains(c.internalDomains);c.receiverUrl=receiver(c.receiverUrl,c.allowHttp).href.replace(/\/$/,'');
    if(c.enabled&&(!c.internalDomains.length||!c.accountIds.length||c.receiverToken.length<32||!c.clientLabel.trim()))throw new Error('Choose accounts, internal domains, client label and a receiver key (at least 32 characters) before enabling.');
    if(c.clientLabel.length>120)throw new Error('Client label must be at most 120 characters.');
    return c;
}
const api={normalizeDomain,domains,internal,receiver,permissionPattern,outgoing,minimalHeader,metadata,settings};
if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.ListenerMail=api;
})(globalThis);
