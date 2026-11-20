(()=>{'use strict';
const $=id=>document.getElementById(id),host=new URLSearchParams(location.search).get('host');let page=1,total=0,selected=null,processPage=1,processTotal=0,controller=null;
const stamp=value=>value?new Date(value).toISOString().replace('T',' ').replace('Z',' UTC'):'—';
function row(body,values){const tr=document.createElement('tr');for(const value of values){const td=document.createElement('td');td.textContent=value||'—';tr.append(td);}$(body).append(tr);return tr;}
async function get(params){const response=await fetch('/api/network-context?'+new URLSearchParams({host,...params}),{signal:controller.signal});const data=await response.json();if(!response.ok)throw Error(data.error||'Unable to read network samples');return data;}
function clear(){selected=null;processPage=1;processTotal=0;for(const id of ['contextNetworks','contextProcesses'])$(id).replaceChildren();$('contextSelection').textContent='Select a minute sample';$('contextNote').textContent='';$('processPrev').disabled=$('processNext').disabled=true;$('processPage').textContent='';}
async function details(snapshot){controller?.abort();controller=new AbortController();selected=snapshot;try{
 const data=await get({snapshot,processPage});$('contextSelection').textContent=stamp(data.snapshot.timestamp_utc);$('contextNote').textContent=(data.snapshot.status==='ok'?'Sample complete. ':'Partial sample. ')+(data.snapshot.note||'');
 $('contextNetworks').replaceChildren();$('contextProcesses').replaceChildren();for(const n of data.networks)row('contextNetworks',[host,n.network_type,n.network_name,n.adapter_name,n.connection_type,n.addresses,n.gateway]);
 for(const p of data.processes)row('contextProcesses',[host,p.entry_type,String(p.pid),p.name,p.path,stamp(p.started_utc),p.vpn_candidate?'Candidate':'No']);
 processTotal=data.snapshot.process_count;$('processPage').textContent='Page '+processPage+' · '+processTotal+' entries';$('processPrev').disabled=processPage===1;$('processNext').disabled=processPage*200>=processTotal;
 }catch(e){if(e.name!=='AbortError')$('contextStatus').textContent=e.message;}}
async function load(){controller?.abort();controller=new AbortController();clear();$('contextSamples').replaceChildren();$('contextStatus').textContent='Loading…';try{
 const data=await get({day:$('contextDay').value,page});total=Number(data.total);$('contextStatus').textContent=!data.available?'Update the Collector and agent to collect network context.':total?'Select a sample to correlate networks with running processes and services.':'No complete samples for this UTC day. Update the agent to enable collection; queued samples appear after delivery.';
 for(const s of data.snapshots){const tr=row('contextSamples',[stamp(s.timestamp_utc),String(s.network_count),String(s.process_count),String(s.vpn_candidates),s.status]);const td=document.createElement('td'),button=document.createElement('button');button.textContent='Inspect sample';button.onclick=()=>{processPage=1;details(s.snapshot_id);};td.append(button);tr.append(td);}
 $('contextPage').textContent='Page '+page+' · '+total+' samples';$('contextPrev').disabled=page===1;$('contextNext').disabled=page*50>=total;
 }catch(e){if(e.name!=='AbortError')$('contextStatus').textContent=e.message;}}
$('contextDay').value=new Date().toISOString().slice(0,10);$('contextToggle').onclick=()=>{const open=$('contextPanel').hidden;$('contextPanel').hidden=!open;$('contextToggle').setAttribute('aria-expanded',String(open));if(open)load();else controller?.abort();};
$('contextFilters').onsubmit=e=>{e.preventDefault();page=1;load();};$('contextPrev').onclick=()=>{if(page>1){page--;load();}};$('contextNext').onclick=()=>{if(page*50<total){page++;load();}};$('processPrev').onclick=()=>{if(selected&&processPage>1){processPage--;details(selected);}};$('processNext').onclick=()=>{if(selected&&processPage*200<processTotal){processPage++;details(selected);}};
})();
