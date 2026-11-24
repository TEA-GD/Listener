'use strict';
const $=id=>document.getElementById(id),params=new URLSearchParams(location.search);
let direction=['incoming','outgoing','unknown'].includes(params.get('direction'))?params.get('direction'):'outgoing',page=1,active=null;
$('timezone').value=params.get('tz')||Intl.DateTimeFormat().resolvedOptions().timeZone||'UTC';
function dateInZone(zone){return new Intl.DateTimeFormat('en-CA',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date());}
try{$('day').value=params.get('day')||dateInZone($('timezone').value);}catch{$('timezone').value='UTC';$('day').value=new Date().toISOString().slice(0,10);}
$('novelty').value=['connections','peers'].includes(params.get('novelty'))?params.get('novelty'):'';
function cell(row,value,title){const td=document.createElement('td');td.textContent=value===null||value===undefined||value===''?'—':value;if(title)td.title=title;row.append(td);}
function time(ms){return new Intl.DateTimeFormat(undefined,{timeZone:$('timezone').value,dateStyle:'medium',timeStyle:'medium'}).format(new Date(ms));}
async function json(url,signal){const response=await fetch(url,{signal});const data=await response.json();if(!response.ok)throw Error(data.error||'Analysis request failed');return data;}
async function load(){
 if(active)active.abort();const request=new AbortController();active=request;$('error').hidden=true;
 const query=new URLSearchParams({host:$('device').value,direction,day:$('day').value,tz:$('timezone').value,novelty:$('novelty').value,q:$('search').value,page:String(page)});
 try{
  const data=await json('/api/analysis?'+query,request.signal);if(active!==request)return;
  for(const button of document.querySelectorAll('[data-direction]'))button.setAttribute('aria-pressed',String(button.dataset.direction===direction));
  $('incomingCount').textContent=data.summary.incoming;$('outgoingCount').textContent=data.summary.outgoing;$('newCount').textContent=data.summary.new_connections;$('peerCount').textContent=data.summary.new_peers;
  $('analysisStatus').textContent=(data.status.catching_up?'Learning historical data — results are provisional. ':'History scan complete. ')+'Last checked: '+(data.status.last_processed_utc?new Date(data.status.last_processed_utc).toLocaleString():'not yet available');
  $('rows').replaceChildren();
  for(const entry of data.rows){const row=document.createElement('tr');cell(row,entry.host);cell(row,direction==='incoming'?entry.peer_ip:entry.local_ip);cell(row,direction==='incoming'?entry.local_ip:entry.peer_ip);cell(row,entry.service_port);cell(row,entry.protocol);cell(row,entry.process_name||'Unknown',entry.software);cell(row,direction==='unknown'?'Unknown':entry.direction_basis==='local_listener'?'Estimated · local listener':'Estimated · no local listener');cell(row,time(entry.first_any_ms),'First observed in this direction: '+time(entry.first_seen_ms));cell(row,time(entry.last_seen_ms));cell(row,entry.observations);$('rows').append(row);}
  $('tableTitle').textContent=direction[0].toUpperCase()+direction.slice(1)+' connections';$('sourceHeading').textContent=direction==='unknown'?'DEVICE IP':'SOURCE IP';$('destinationHeading').textContent=direction==='unknown'?'PEER IP':'DESTINATION IP';
  $('total').textContent=data.total+' unique connections';$('empty').hidden=data.rows.length>0;$('page').textContent='Page '+page+' of '+Math.max(1,Math.ceil(data.total/data.size));$('previous').disabled=page<=1;$('next').disabled=page*data.size>=data.total;
  history.replaceState(null,'','analyze.html?'+query);$('back').href='index.html'+($('device').value?'?host='+encodeURIComponent($('device').value):'');
 }catch(error){if(error.name!=='AbortError'){$('error').textContent=error.message;$('error').hidden=false;$('analysisStatus').textContent='Analysis unavailable. Check that the independent analyzer is running and the GUI can read its database.';$('rows').replaceChildren();$('previous').disabled=$('next').disabled=true;}}
}
$('analysisFilters').addEventListener('submit',e=>{e.preventDefault();page=1;void load();});$('refresh').addEventListener('click',()=>void load());
for(const button of document.querySelectorAll('[data-direction]'))button.addEventListener('click',()=>{direction=button.dataset.direction;page=1;void load();});
$('previous').addEventListener('click',()=>{page--;void load();});$('next').addEventListener('click',()=>{page++;void load();});
(async()=>{try{const devices=await json('/api/analysis/devices');const selected=params.get('host')||'';for(const d of devices){const option=document.createElement('option');option.value=d.host;option.textContent=d.host;$('device').append(option);}if(selected&&!devices.some(d=>d.host===selected)){const option=document.createElement('option');option.value=selected;option.textContent=selected;$('device').append(option);}$('device').value=selected;await load();}catch(error){$('error').textContent=error.message;$('error').hidden=false;$('analysisStatus').textContent='Run the independent connection analyzer and set WATCHER_ANALYSIS_DB_PATH for the GUI.';}})();
