/* Name annotations are independent of recorded events and never delay rendering. */
(()=>{
'use strict';
const cache=new Map(),attempts=new Map();let timer=null,running=false;
function schedule(delay=0){if(timer===null)timer=setTimeout(update,delay);}
function eligible(ip){
    if(!ip)return false;
    if(ip.includes(':')){try{new URL('http://['+ip+']/');return !ip.includes('%');}catch{return false;}}
    return /^\d{1,3}(\.\d{1,3}){3}$/.test(ip)&&ip.split('.').every(part=>Number(part)<=255);
}
function cell(ip,reported,title){
    const td=document.createElement('td');td.textContent=reported||'—';
    if(reported){td.title=title;return td;}
    td.title='Name unavailable';
    if(eligible(ip)){td.dataset.nameIp=ip;schedule();}
    return td;
}
function paint(td,result){td.textContent=result.name||'—';td.title=result.name?'Current reverse DNS name; may differ from the website hostname':'Name unavailable';}
async function update(){
    timer=null;if(document.hidden)return;
    if(running){schedule(250);return;}
    const needed=new Map();const now=Date.now();
    for(const td of document.querySelectorAll('td[data-name-ip]')){
        if(td.closest('[hidden]'))continue;
        const ip=td.dataset.nameIp,saved=cache.get(ip);
        if(saved&&saved.expires>now){paint(td,saved);continue;}
        const attempt=attempts.get(ip);
        if(attempt&&attempt.expires<=now)attempts.delete(ip);
        else if(attempt&&attempt.count>=60)continue;
        if(!needed.has(ip))needed.set(ip,[]);needed.get(ip).push(td);
    }
    if(!needed.size)return;
    running=true;let pending=false;
    try{
        const ips=[...needed.keys()];
        for(let start=0;start<ips.length;start+=50){
            const batch=ips.slice(start,start+50),params=new URLSearchParams();
            for(const ip of batch){params.append('ip',ip);const attempt=attempts.get(ip)||{count:0,expires:Date.now()+120000};attempts.set(ip,{...attempt,count:attempt.count+1});}
            const response=await fetch('/api/connection-names?'+params,{signal:AbortSignal.timeout(5000)});
            if(!response.ok)throw Error('Name lookup unavailable');
            const data=await response.json();
            for(const ip of batch){
                const result=data.names[ip]||{name:null,status:'unavailable'};
                if(result.status==='pending'||result.status==='busy'){pending=true;continue;}
                if(cache.size>=2048)cache.delete(cache.keys().next().value);
                cache.set(ip,{...result,expires:Date.now()+(result.name?600000:60000)});attempts.delete(ip);
                for(const td of needed.get(ip))if(td.isConnected)paint(td,result);
            }
        }
    }catch{
        for(const ip of needed.keys())cache.set(ip,{name:null,expires:Date.now()+60000});
    }finally{
        running=false;
        // Bound state even when many different devices are visited in a long-lived browser tab.
        while(cache.size>2048)cache.delete(cache.keys().next().value);
        while(attempts.size>2048)attempts.delete(attempts.keys().next().value);
        if(pending)schedule(1000);
    }
}
window.ListenerNames={
    source(row,host){return cell(row.SOURCE_IP,row.SOURCE_DOMAIN_NAME||row.MONITORED_HOST||host,'Agent-reported source device name');},
    destination(row){return cell(row.RECIEVER_IP,row.RECIEVER_DOMAINNAME,'Agent-reported destination name');}
};
document.addEventListener('visibilitychange',()=>{if(!document.hidden)schedule();});
document.addEventListener('listener-tab',()=>schedule());
})();
