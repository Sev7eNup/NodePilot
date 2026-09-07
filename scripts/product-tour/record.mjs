import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { responseFor, WF, EXEC } from './demo-data.mjs';
import { settingsAndLogResponse } from './settings-log-data.mjs';
import { startFrameCapture } from './capture-frames.mjs';
import { demoQuestion, demoChatStream } from './chat-data.mjs';
import { createImportDemo, importFile, importedName } from './import-data.mjs';
import { installDefaultMocks } from '../../src/nodepilot-ui/e2e/fixtures/mockApi.ts';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const require = createRequire(path.join(root,'src/nodepilot-ui/package.json'));
const { chromium } = require('playwright');
const base = process.env.TOUR_BASE_URL || 'http://localhost:5173';
if (!['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname)) throw new Error('Use a loopback frontend for this capture.');
const out = path.resolve(root, process.env.TOUR_OUTPUT || 'out/product-tour-liveops-v2');
const previousCapture=await readFile(path.join(out,'capture.json'),'utf8').then(JSON.parse).catch(()=>null);
const preview = process.argv.includes('--preview');
const only = process.argv.find(arg=>arg.startsWith('--only='))?.slice(7);
if(only && !['00-intro','00-import','01-designer','02-history','03-dashboard','04-liveops','05-log','06-chat','07-ai','08-auth','09-outro'].includes(only)) throw new Error('Unknown --only segment.');
await mkdir(path.join(out,'raw'),{recursive:true});
const browser = await chromium.launch({headless:true});
const context = await browser.newContext({viewport:{width:2560,height:1440},deviceScaleFactor:1,locale:'en-US',colorScheme:'dark',serviceWorkers:'block'});
const page = await context.newPage();
page.setDefaultTimeout(18000);
const errors=[]; const unknown=new Set();
page.on('pageerror',e=>errors.push(e.message));
page.on('console',m=>{if(m.type()==='error' && !/404|SignalR|WebSocket|connection|Failed to load resource/.test(m.text())) {errors.push(m.text());console.error(m.text());}});
// Context guard also covers iframe requests. Only this local frontend may be read.
await context.route('**/*', async route=> {
  const u=new URL(route.request().url());
  if(u.origin!==new URL(base).origin) return route.abort('blockedbyclient');
  if(!['GET','HEAD'].includes(route.request().method()) && !u.pathname.startsWith('/api/') && !u.pathname.startsWith('/hubs/')) return route.abort('blockedbyclient');
  return route.continue();
});
await installDefaultMocks(page);
const importDemo = createImportDemo();
await page.route(u=>u.pathname.startsWith('/api/'), async route=>{
  const request=route.request();
  if(request.method()==='POST' && new URL(request.url()).pathname==='/api/workflows/import-scorch') {
    const body=importDemo.accept(request);
    await page.waitForTimeout(preview?50:700);
    return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(body)});
  }
  if(request.method()==='POST' && new URL(request.url()).pathname==='/api/ai/knowledge/ask') {
    await page.waitForTimeout(preview?50:300);
    return route.fulfill({status:200,contentType:'text/event-stream',body:demoChatStream(request.postDataJSON())});
  }
  if(request.method()!=='GET') throw new Error(`Unexpected mutation during tour: ${request.method()} ${new URL(request.url()).pathname}`);
  const u=new URL(request.url()); const body=importDemo.response(u) ?? settingsAndLogResponse(u) ?? responseFor(u);
  if(body===undefined){unknown.add(u.pathname); return route.fallback();}
  return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(body)});
});
await page.addInitScript(()=>{
  localStorage.setItem('nodepilot.theme',JSON.stringify({state:{theme:'dark'},version:0}));
  localStorage.setItem('nodepilot.lang.store',JSON.stringify({state:{lang:'en'},version:0}));
  localStorage.setItem('i18nextLng','en');
  localStorage.setItem('nodepilot.scriptEditor.fontSize','17');
  localStorage.setItem('nodepilot.supportEvents.tableHeight.v2','620');
  localStorage.setItem('nodepilot.supportLog.plainHeight.v2','630');
  localStorage.setItem('nodepilot-design',JSON.stringify({state:{designerMode:'expert',toolbarLayout:'compact',nodeStyle:'classic',nodeScaleIndex:4,labelFontOffsetIndex:2,edgesAnimated:true},version:4}));
});
const stage=await readFile(path.join(here,'stage.html'),'utf8');
await page.route('**/__product-tour',route=>route.fulfill({contentType:'text/html',body:stage}));
await page.goto(`${base}/__product-tour`);
await page.evaluate(()=>document.fonts.ready);
const frame=page.frame({name:'app'});
const pause=ms=>page.waitForTimeout(preview?Math.min(ms,180):ms);
async function go(url){
  await page.evaluate(()=>document.getElementById('card').style.display='none');
  await frame.goto(`${base}${url}`);
  await frame.locator('#root').waitFor();
  await frame.evaluate(()=>document.fonts.ready);
  await page.waitForTimeout(1200);
}
async function scrollMainTo(locator, behavior='instant'){
  await locator.evaluate((el,behavior)=>{
    const scroller=document.getElementById('np-main-scroll');
    scroller.scrollTo({top:scroller.scrollTop+el.getBoundingClientRect().top-scroller.getBoundingClientRect().top-16,behavior});
  },behavior);
}
async function point(locator,click=false){
  await locator.waitFor({state:'visible'});
  const b=await locator.boundingBox(); if(!b) throw new Error('Missing target box');
  const x=b.x+b.width/2,y=b.y+b.height/2;
  await page.evaluate(({x,y})=>{const p=document.getElementById('pointer');p.style.opacity='1';p.style.left=(x*.75)+'px';p.style.top=(y*.75)+'px';},{x,y});
  await pause(650);
  await page.mouse.move(x,y);
  if(click){
    await page.evaluate(({x,y})=>{const p=document.getElementById('pulse');p.classList.remove('pulse');p.style.left=(x*.75-24)+'px';p.style.top=(y*.75-24)+'px';void p.offsetWidth;p.classList.add('pulse');},{x,y});
    // A running timeline bar continuously changes width; a native click avoids waiting
    // for an animation to become stable. Each scene asserts the resulting UI state.
    await page.mouse.click(x,y);
  }
}
async function chapter(i,title,description){
  await page.evaluate(({i,title,description})=>{
    document.getElementById('card').style.display='none';
    document.getElementById('pointer').style.opacity='0';
    document.getElementById('eyebrow').textContent=['','01 · Design','02 · Inspect','03 · Observe','04 · Operate','05 · Logs','06 · AI chat','07 · AI settings','08 · Identity · Preview'][i];
    document.getElementById('title').textContent=title;
    document.getElementById('description').textContent=description;
    document.getElementById('counter').textContent=`0${i} / 08`;
    document.getElementById('progress').style.width=(i*100/8)+'%';
  },{i,title,description});
}
const segments=[];
async function clip(name,duration,action,{endAfterAction=false}={}){
  if(only && name!==only){
    const previous=previousCapture?.preview===false ? previousCapture.segments.find(segment=>segment.name===name) : null;
    if(!previous) throw new Error(`Record a full tour before replacing one scene: missing ${name}`);
    segments.push(previous);return;
  }
  console.log(`Recording ${name}`);
  await page.screenshot({path:path.join(out,`raw/${name}-start.png`)});
  const capture=preview ? null : await startFrameCapture(page,out,name);
  const start=capture?.started ?? performance.now();
  if(action) await action();
  if(!preview && performance.now()-start > duration*1000) throw new Error(`${name}: interaction exceeded the planned duration`);
  if(!preview && endAfterAction) duration=Math.ceil((performance.now()-start)/1000*30)/30;
  if(!preview) await page.waitForTimeout(Math.max(0,duration*1000-(performance.now()-start)));
  const recording = await capture?.stop(duration);
  await page.screenshot({path:path.join(out,`raw/${name}-end.png`)});
  segments.push({name,duration,...recording});
}
try{
  await page.evaluate(()=>document.getElementById('card').style.display='block');
  await clip('00-intro',4);
  await go('/workflows');
  await frame.getByRole('button',{name:'Import SCOrch',exact:true}).waitFor();
  await chapter(0,'Import your SCOrch runbooks.','1. Choose Import SCOrch and select a .ois_export file.');
  await page.evaluate(()=>{
    document.getElementById('eyebrow').textContent='IMPORT · SCOrch';
    document.getElementById('counter').textContent='.ois_export';
  });
  await clip('00-import',12,async()=>{
    await pause(1000);
    const chooserReady=page.waitForEvent('filechooser');
    await point(frame.getByRole('button',{name:'Import SCOrch',exact:true}),true);
    const chooser=await chooserReady;
    await chooser.setFiles(fileURLToPath(importFile));
    await frame.getByRole('heading',{name:'Imported 1 runbook from Daily-Log-Archive.ois_export',exact:true}).waitFor();
    await page.evaluate(()=>document.getElementById('description').textContent='2. Review the imported activities. Workflows start disabled.');
    await pause(3000);
    await page.screenshot({path:path.join(out,'raw/00-import-review.png')});
    const importModal=frame.getByRole('heading',{name:'Imported 1 runbook from Daily-Log-Archive.ois_export',exact:true}).locator('../..');
    await point(importModal.getByRole('button',{name:importedName,exact:true}),true);
    await frame.locator('.react-flow__node[data-id="70707070-0000-0000-0000-000000000002"]').waitFor();
    await frame.getByRole('button',{name:'Collapse execution panel',exact:true}).click();
    await frame.locator('.react-flow__controls-fitview').click();
    await page.evaluate(()=>document.getElementById('description').textContent='3. Open the imported workflow to inspect the converted steps.');
    await pause(2300);
  });
  await go(`/workflows/${WF}`);
  await frame.locator('.react-flow__node[data-id="disk"]').waitFor();
  await frame.getByRole('button',{name:'Collapse execution panel',exact:true}).click();
  await frame.locator('.react-flow__controls-fitview').click();
  await page.waitForTimeout(700);
  await chapter(1,'Combine the right tools for the job.','Service checks, PowerShell, File Copy and LLM summaries in one workflow.');
  await clip('01-designer',15,async()=>{
    await pause(1800);
    await point(frame.locator('.react-flow__node[data-id="archive"]'),true);
    await frame.locator('input[value$="daily-health.log"]').last().waitFor();
    await frame.locator('.react-flow__controls-fitview').click();
    await pause(1100);
    await point(frame.locator('.react-flow__node[data-id="summary"]'),true);
    await pause(1100);
    await point(frame.locator('.react-flow__node[data-id="disk"]'),true);
    await frame.getByRole('button',{name:'Open Editor',exact:true}).waitFor();
    await frame.locator('.react-flow__controls-fitview').click();
    await pause(650);
    await point(frame.getByRole('button',{name:'Open Editor',exact:true}),true);
    await frame.locator('.monaco-editor').first().waitFor();
    if(await frame.getByRole('button',{name:'Generate script with AI',exact:true}).innerText()!=='AI') throw new Error('English AI label missing');
    await pause(3500);
  },{endAfterAction:true});
  // Prepare History with the real shared Gantt component before the next clip.
  await go(`/workflows/${WF}`);
  await frame.getByRole('button',{name:/history/i}).click();
  await frame.locator(`[data-row-id="${EXEC}"]`).click();
  await frame.getByRole('button',{name:/^gantt$/i}).click();
  await frame.getByTestId('gantt-chart').waitFor();
  const resize=await frame.locator('[role="separator"][aria-orientation="horizontal"]').boundingBox();
  await page.mouse.move(resize.x+resize.width/2,resize.y+resize.height/2);
  await page.mouse.down();
  await page.mouse.move(resize.x+resize.width/2,resize.y-100,{steps:15});
  await page.mouse.up();
  await frame.locator('.react-flow__controls-fitview').click();
  await page.waitForTimeout(600);
  await chapter(2,'See what happened at every step.','Execution history, parallel timings and the output behind each activity.');
  await clip('02-history',10,async()=>{
    await pause(1800);
    await point(frame.getByTestId('gantt-chart').getByText('Check disk space',{exact:true}));
    await pause(1400);
    await point(frame.getByRole('button',{name:/^list$/i}),true);
    await pause(700);
    await point(frame.getByRole('button',{name:/Check disk space.*runScript/i}),true);
    await frame.getByText('C: 142.6 GB free (57.1%)',{exact:true}).waitFor();
    await frame.getByText('C: 142.6 GB free (57.1%)',{exact:true}).scrollIntoViewIfNeeded();
    await pause(1700);
  });
  await go('/');
  await frame.getByText('Morning Fleet Health Check').first().waitFor();
  await chapter(3,'Know how your automation is doing.','Execution trends, workflow health and your Windows machines in one overview.');
  await clip('03-dashboard',4,async()=>{
    await pause(450);
    await frame.evaluate(duration=>new Promise(resolve=>{
      const scroller=document.getElementById('np-main-scroll');
      const target=scroller.scrollHeight-scroller.clientHeight;
      const start=performance.now();
      const tick=now=>{const p=Math.min(1,(now-start)/duration);scroller.scrollTop=target*(p*p*(3-2*p));if(p<1)requestAnimationFrame(tick);else resolve();};
      requestAnimationFrame(tick);
    }),preview?250:2600);
  });
  await go('/operations');
  await frame.getByTitle(/Nightly Backup · Running/).waitFor();
  await chapter(4,'Keep an eye on live operations.','Running now, recently completed and scheduled next. Open a run for details.');
  await clip('04-liveops',7,async()=>{
    await pause(2800);
    await point(frame.getByTitle(/Nightly Backup · Running/),true);
    await frame.getByLabel('Execution details').waitFor();
    await pause(2500);
  });
  const logLink = frame.locator('a[href="/support-log"]').first();
  await logLink.scrollIntoViewIfNeeded();
  await chapter(5,'Follow the details in your logs.','Structured events and a plain-text log view, directly from the main menu.');
  await clip('05-log',10,async()=>{
    await pause(650);
    await point(logLink,true);
    await frame.getByRole('heading',{name:'Support log',exact:true}).waitFor();
    await frame.getByText('All checks passed: services, disk capacity and event log.',{exact:true}).first().waitFor();
    await pause(3700);
    await point(frame.getByRole('button',{name:/Plain-text/}),true);
    await frame.locator('pre').filter({hasText:'Health report published.'}).waitFor();
    await pause(1500);
  });
  const chatLink=frame.locator('a[href="/ai-chat"]').first();
  await chatLink.scrollIntoViewIfNeeded();
  await chapter(6,'Ask your automation a question.','AI Chat brings workflow and operations context into the conversation.');
  await clip('06-chat',8.5,async()=>{
    await point(chatLink,true);
    await frame.locator('#np-main-scroll').getByRole('heading',{name:'AI Chat',exact:true}).waitFor();
    const input=frame.getByRole('textbox',{name:'Ask about NodePilot, your workflows, or the source code…',exact:true});
    await pause(500);
    await input.pressSequentially(demoQuestion,{delay:preview?1:22});
    await point(frame.getByRole('button',{name:'Send',exact:true}),true);
    await frame.getByText('No action required.',{exact:true}).waitFor();
    await pause(2700);
  });
  await go('/settings?tab=system&section=integrations');
  const llm = frame.getByRole('heading',{name:'LLM (AI)',exact:true});
  await llm.waitFor();
  await scrollMainTo(llm);
  await frame.getByRole('tab',{name:/Local Ollama/}).waitFor();
  await page.waitForTimeout(400);
  await chapter(7,'Bring your own AI model.','Configure local or hosted models, profiles and tool calling.');
  await clip('07-ai',6,async()=>{
    await pause(1400);
    await point(frame.getByRole('tab',{name:'Cloud API',exact:true}),true);
    await frame.locator('input[value="demo-model"]').waitFor();
    await pause(1300);
    await point(frame.getByRole('tab',{name:/Local Ollama/}),true);
  });
  await go('/settings?tab=system&section=authentication');
  await frame.getByRole('heading',{name:'LDAP',exact:true}).waitFor();
  await chapter(8,'Use your existing identity provider.','LDAP / LDAPS · Windows SSO (Kerberos) · OpenID Connect');
  await clip('08-auth',9,async()=>{
    await pause(3000);
    await scrollMainTo(frame.getByRole('heading',{name:'Windows Integrated Auth (Negotiate)',exact:true}),'smooth');
    await pause(1000);
    await frame.getByRole('heading',{name:'OpenID Connect (OIDC)',exact:true}).waitFor();
    await point(frame.getByRole('heading',{name:'OpenID Connect (OIDC)',exact:true}));
    await pause(1500);
  });
  await page.evaluate(()=>{
    document.getElementById('card').style.display='block';
    document.getElementById('kicker').textContent='Open source · Apache-2.0';
    document.getElementById('headline').innerHTML='Make Windows automation<br><em>easier to follow.</em>';
    document.getElementById('sub').textContent='Explore NodePilot. Try a workflow. Tell us what you think.';
    document.getElementById('tags').innerHTML='<span>Visual workflows</span><span>Agentless execution</span><span>Self-hosted</span>';
    document.getElementById('url').textContent='github.com/Sev7eNup/NodePilot';
    document.getElementById('card-foot').textContent='Built for Windows operations';
  });
  await clip('09-outro',5);
  await writeFile(path.join(out,'capture.json'),JSON.stringify({segments,errors,unhandledApi:[...unknown],preview,width:2560,height:1440,uiScale:.88},null,2));
  console.log(JSON.stringify({output:out,errors,unhandledApi:[...unknown]}));
  if(errors.length) process.exitCode=1;
}catch(error){
  console.error({errors,unhandledApi:[...unknown]});
  await page.screenshot({path:path.join(out,'raw/failure.png')}).catch(()=>{});
  await writeFile(path.join(out,'raw/failure.txt'),await frame.locator('body').innerText().catch(()=>''));
  console.error(error);process.exitCode=1;
}finally{await browser.close();}
