'use strict';
const $=id=>document.getElementById(id);
async function refresh(){try{const s=await browser.runtime.sendMessage({type:'status'});$('status').textContent=`Pending alerts: ${s.pending||0}\nLast delivery: ${s.lastDelivered||'none'}\nLast error: ${s.lastError||'none'}`;}catch(e){$('status').textContent=e.message;}}
(async()=>{
    const c=ListenerMail.settings((await browser.storage.local.get('settings')).settings||{});
    $('enabled').checked=c.enabled;$('domains').value=c.internalDomains.join(', ');$('subdomains').checked=c.includeSubdomains;$('receiver').value=c.receiverUrl;$('token').value=c.receiverToken;$('clientLabel').value=c.clientLabel;$('allowHttp').checked=c.allowHttp;
    const accounts=await browser.accounts.list(false);
    for(const account of accounts){const label=document.createElement('label');label.className='check';const input=document.createElement('input');input.type='checkbox';input.value=account.id;input.checked=c.accountIds.includes(account.id);label.append(input,document.createTextNode(account.name+' '+(account.identities||[]).map(i=>i.email).join(', ')));$('accounts').append(label);}
    await refresh();
})().catch(e=>$('status').textContent=e.message);
$('settings').onsubmit=async e=>{
    e.preventDefault();try{
        const c=ListenerMail.settings({enabled:$('enabled').checked,internalDomains:$('domains').value,includeSubdomains:$('subdomains').checked,accountIds:[...$('accounts').querySelectorAll('input:checked')].map(i=>i.value),receiverUrl:$('receiver').value,receiverToken:$('token').value,clientLabel:$('clientLabel').value.trim(),allowHttp:$('allowHttp').checked});
        const origin=ListenerMail.permissionPattern(c.receiverUrl);
        if(c.enabled&&!await browser.permissions.request({origins:[origin]}))throw new Error('Receiver access was not granted. Settings not saved.');
        await browser.storage.local.set({settings:c});$('status').textContent='Settings saved. New incoming mail will be processed according to these settings.';
    }catch(error){$('status').textContent=error.message;}
};
$('refresh').onclick=refresh;
$('retry').onclick=async()=>{try{await browser.runtime.sendMessage({type:'retry'});await refresh();}catch(e){$('status').textContent=e.message;}};
$('test').onclick=async()=>{try{await browser.runtime.sendMessage({type:'test'});$('status').textContent='Synthetic test queued. Refresh status shortly to check delivery.';}catch(e){$('status').textContent=e.message;}};
