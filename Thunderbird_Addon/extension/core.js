/* Shared pure extraction/filter helpers; textParts preserve decoded MIME text verbatim. */
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
function mimeBody(full,attachments,htmlToText){
    const blocked=new Set(attachments.map(a=>a.partName).filter(Boolean)),parts=[];
    function walk(part,isRoot=false){
        if(!part)return '';
        const type=String(part.contentType||'').split(';')[0].toLowerCase();
        const disposition=(part.headers?.['content-disposition']||[]).join(' ');
        if(part.name||blocked.has(part.partName)||/^attachment\b/i.test(disposition)||(type==='message/rfc822'&&!isRoot))return '';
        if(Array.isArray(part.parts)&&part.parts.length){
            const values=part.parts.map(p=>({part:p,text:walk(p)}));
            if(type==='multipart/alternative')return (values.find(p=>String(p.part.contentType||'').toLowerCase().startsWith('text/plain')&&p.text)||values.find(p=>p.text))?.text||'';
            return values.map(p=>p.text).filter(Boolean).join('\n\n');
        }
        if(type.startsWith('text/')&&typeof part.body==='string'){
            parts.push({content_type:type,text:part.body,part_name:String(part.partName||'')});
            return type==='text/html'?htmlToText(part.body):part.body;
        }
        return '';
    }
    return {text:walk(full,true),parts};
}
function settings(value){
    const c={enabled:false,internalDomains:[],includeSubdomains:true,accountIds:[],receiverUrl:'http://127.0.0.1:6516',receiverToken:'',clientLabel:'',allowHttp:false,...value};
    c.internalDomains=domains(c.internalDomains);c.receiverUrl=receiver(c.receiverUrl,c.allowHttp).href.replace(/\/$/,'');
    if(c.enabled&&(!c.internalDomains.length||!c.accountIds.length||c.receiverToken.length<32||!c.clientLabel.trim()))throw new Error('Choose accounts, internal domains, client label and a receiver key (at least 32 characters) before enabling.');
    if(c.clientLabel.length>120)throw new Error('Client label must be at most 120 characters.');
    return c;
}
const api={normalizeDomain,domains,internal,receiver,permissionPattern,outgoing,mimeBody,settings};
if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.ListenerMail=api;
})(globalThis);
