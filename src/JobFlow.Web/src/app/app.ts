import { Component, signal, computed, OnInit, OnDestroy, HostListener, ChangeDetectorRef, inject } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { type Field, type Row, workspaceSections } from './workspace-sections';
import { WorkspaceDashboard } from './workspace-dashboard';
import { requestApi, readCollections } from './workspace-api';
import { runStatusLabels, workspaceTime } from './workspace-format';
type CliModel = {id:string;displayName:string;isDefault:boolean;defaultReasoningEffort:string;supportedReasoningEfforts:string[]};
type CliModels = {models:CliModel[];defaultModel:string;fetchedAt:string};
@Component({selector:'app-root',imports:[CommonModule,FormsModule,WorkspaceDashboard],templateUrl:'./app.html',styleUrl:'./app.css'})
export class App implements OnInit, OnDestroy {
  modelCatalog=signal<CliModels|null>(null);modelsLoading=signal(false);modelsError=signal('');
  taskModelPolicy=signal<Row[]>([]);
  recommendedTaskModels(){return this.taskModelPolicy().filter(x=>x['taskType']===this.draft['taskType']);}
  private modelsRequest:AbortController|null=null;
  async loadModels(refresh=false){
    this.modelsRequest?.abort();
    this.modelCatalog.set(null);this.modelsError.set('');this.modelsLoading.set(false);
    const section=this.section(),draft=this.draft;
    if(!['agents','providers'].includes(section)||!this.editing())return;
    if(section==='agents'&&!draft['modelProviderId'])return;
    const request=new AbortController();this.modelsRequest=request;this.modelsLoading.set(true);
    const query=section==='agents'?'providerId='+encodeURIComponent(draft['modelProviderId']):'kind='+encodeURIComponent(draft['kind']??'codex-cli');
    try{
      const result:CliModels=await this.api('provider-models?'+query+'&refresh='+refresh,'GET',undefined,request.signal);
      if(this.modelsRequest===request&&this.draft===draft&&this.section()===section){this.modelCatalog.set(result);if(section==='agents')this.taskModelPolicy.set(await this.api('agent-model-policy','GET',undefined,request.signal));}
    }catch(error){if(!request.signal.aborted&&this.modelsRequest===request)this.modelsError.set((error as Error).message);}
    finally{if(this.modelsRequest===request){this.modelsLoading.set(false);this.changeDetector.markForCheck();}}
  }
  inheritedModel(){return this.section()==='agents'?(this.data()['providers']??[]).find(x=>x['id']===this.draft['modelProviderId'])?.['model']||this.modelCatalog()?.defaultModel||'':this.modelCatalog()?.defaultModel||'';}
  inheritedModelLabel(){const id=this.inheritedModel();return (this.section()==='agents'?'Модель провайдера':'По умолчанию CLI')+(id?' · '+id:'');}
  missingSavedModel(){const id=this.draft['model'];return !!id&&!(this.modelCatalog()?.models??[]).some(x=>x.id===id);}
  selectedModel(){return this.modelCatalog()?.models.find(x=>x.id===(this.draft['model']||this.inheritedModel()));}
  reasoningWarning(){if(this.draft['useTaskModelPresets'])return '';const value=this.draft['reasoning'],model=this.selectedModel();return value&&model&&!model.supportedReasoningEfforts.includes(value)?'Сохранённый reasoning '+value+' не поддерживается этой моделью. Выберите доступный уровень.':'';}
  dossierVersion:Row|null=null;dossierAgentId='';
  dossierAgents(){return (this.data()['agents']??[]).filter(x=>x['enabled']&&x['candidateProfileId']===this.dossierVersion?.['candidateProfileId']);}
  async proposeDossier(){if(!this.dossierVersion||!this.dossierAgentId)return;const row=this.dossierVersion;if(!await this.command('resumes/versions/'+row['versionId']+'/dossier',{candidateProfileId:row['candidateProfileId'],agentId:this.dossierAgentId}))return;this.dossierVersion=null;this.navigate('dossier-proposals');}
  async confirmDossier(row:Row){if(confirm('Заменить опыт, навыки, образование, языки и досье профиля этим предложением? Просмотрите его перед подтверждением.'))await this.command('dossier-proposals/'+row['id']+'/confirm');}
  safeLink(value:any){return typeof value==='string'&&/^https?:\/\//i.test(value)?value:null;}
  private readonly changeDetector=inject(ChangeDetectorRef);
  section=signal('dashboard'); data=signal<Record<string,Row[]>>({});
  loading=signal(false); saving=signal(false); error=signal(''); message=signal(''); result=signal(''); editing=signal(false); query=signal('');
  private selectedProfile = signal('');
  private selectDefaultProfile = true;
  get profileId() { return this.selectedProfile(); }
  set profileId(value: string) {
    if (value === this.profileId) return;
    this.selectedProfile.set(value);
    this.applicationRequest?.abort();
    this.applicationDetail.set(null);
    this.applicationStatusEditing = false; this.applicationNextState = '';
    this.applicationClarificationText = ''; this.clarificationDraftRevision = null;
    this.prepareTarget = null; this.resumeId = ''; this.file = null;
    this.dossierVersion = null; this.dossierAgentId = ''; this.activeAgent = null; this.agentInput = '';
    this.draftApplication = null; this.browserDraft = null; this.browserPreview = null;
    this.externalReview = null; this.externalReviewApplication = null; this.feedbackEvent = null;
    this.importIntegration = null; this.csvText = ''; this.sheetIntegration = null; this.sheetPreview = null;
    this.secretConnection = null; this.secretJson = '{}'; this.platformLogin = null; this.googleConnection = null;
    if (this.remoteBrowserId) this.closeRemoteBrowser();
    this.result.set(''); this.editing.set(false); this.draft = {};
  }
  draft:Row={}; file:File|null=null; resumeId=''; agentInput=''; activeAgent:Row|null=null;
  feedbackEvent:Row|null=null;feedbackApplicationId='';
  feedbackApplications(){return (this.data()['applications']??[]).filter(x=>x['candidateProfileId']===this.feedbackEvent?.['candidateProfileId']&&['Submitted','EmployerResponded'].includes(x['state']));}
  async linkFeedback(){if(!this.feedbackEvent)return;await this.command('feedback/'+this.feedbackEvent['id']+'/link',{applicationId:this.feedbackApplicationId});if(!this.error())this.feedbackEvent=null;}
  importIntegration:Row|null=null; csvText='';
  async selectCsv(event:Event){const file=(event.target as HTMLInputElement).files?.[0];if(file){if(file.size>5242880){this.error.set('Максимум 5 МБ.');return;}this.csvText=await file.text();}}
  async previewImport(){if(this.importIntegration)await this.command('integrations/'+this.importIntegration['id']+'/preview',{csv:this.csvText});}
  async declareNoPriorJournal(row:Row){
    if(!confirm('У этой интеграции нет старого журнала откликов? Это разрешит ручное одобрение, но не отправит отклики.'))return;
    await this.action(async()=>{
      await this.api('integrations/'+row['id']+'/no-prior-journal','POST',{});
      this.message.set('Отмечено: старого журнала нет. Отклики не отправлены.');
    });
  }
  sheetIntegration:Row|null=null;
  sheetTab='JobFlow Journal';
  sheetFile='';
  sheetPlatformIds:string[]=[];
  sheetAllPlatforms=false;
  sheetStartNewJournal=false;
  sheetPreview:{tab:string;digest:string;existing:string[][];rows:string[][]}|null=null;
  openSheetExport(row:Row){
    this.sheetIntegration=row;
    this.sheetTab='JobFlow Journal';
    this.sheetFile='';
    this.sheetPlatformIds=row['jobPlatformId']?[row['jobPlatformId']]:[];
    this.sheetAllPlatforms=false;
    this.sheetStartNewJournal=false;
    this.sheetPreview=null;
  }
  toggleSheetPlatform(id:string, enabled:boolean){
    this.sheetPlatformIds=enabled?[...new Set([...this.sheetPlatformIds,id])]:this.sheetPlatformIds.filter(value=>value!==id);
    this.sheetPreview=null;
  }
  private sheetTarget(){
    return {tab:this.sheetTab.trim(),spreadsheet:this.sheetFile.trim(),allPlatforms:this.sheetAllPlatforms,
      platformIds:this.sheetAllPlatforms?[]:this.sheetPlatformIds,startNewJournal:this.sheetStartNewJournal};
  }
  canPreviewSheet():boolean{
    return !!this.sheetIntegration && !!this.sheetTab.trim() &&
      (this.sheetAllPlatforms || this.sheetPlatformIds.length>0) &&
      (this.sheetStartNewJournal ? !!this.sheetFile.trim() : !!this.sheetIntegration['importConfirmed']);
  }
  async previewSheet(){
    if(!this.sheetIntegration||!this.canPreviewSheet())return;
    const integration=this.sheetIntegration;
    const target=this.sheetTarget();
    this.sheetPreview=null;
    await this.action(async()=>{
      const preview=await this.api('integrations/'+integration['id']+'/sheets/preview','POST',target);
      if(this.sheetIntegration===integration)this.sheetPreview=preview;
    });
  }
  async confirmSheet(){
    if(!this.sheetIntegration||!this.sheetPreview||this.sheetPreview.tab!==this.sheetTab.trim())return;
    const {digest}=this.sheetPreview;
    const integration=this.sheetIntegration;
    this.sheetPreview=null;
    await this.command('integrations/'+integration['id']+'/sheets/confirm',{...this.sheetTarget(),digest});
  }
  googleConnection:Row|null=null;
  async connectGoogle(row:Row){
    const remote=!['127.0.0.1','localhost','[::1]','::1'].includes(location.hostname);
    if(remote){this.closeRemoteBrowser();this.googleConnection=row;this.error.set('');return;}
    await this.action(async()=>{
      const result=await this.api('integrations/'+row['id']+'/google/connect','POST',{});
      location.assign(result.authorizationUrl);
    });
  }
  secretConnection:Row|null=null; secretJson='{}';
  async saveSecrets(){if(!this.secretConnection)return;await this.action(async()=>{await this.api((this.secretConnection!['kind']?'integrations/':'mcp/')+this.secretConnection!['id']+'/secrets','POST',JSON.parse(this.secretJson));this.secretJson='{}';this.secretConnection=null;this.message.set('Секреты сохранены отдельно от конфигурации.');});}
  draftApplication:Row|null=null; draftLetter='';
  applicationDetail=signal<Row|null>(null);applicationDetailLoading=signal(false);applicationDetailError=signal('');
  applicationStatusEditing=false;applicationNextState='';private applicationStatusRevision=0;
  applicationClarificationText='';clarificationOutsideAgeLimit=false;private clarificationDraftRevision:number|null=null;
  updateApplicationClarification(value:string){this.applicationClarificationText=value;this.clarificationDraftRevision=this.applicationDetail()?.['application'].revision??null;}
  applicationClarificationQuestions(detail:Row):string[]{
    if (!detail['canClarify'] || detail['evaluationIsCurrent'] === false || !detail['evaluation']) return [];
    return this.readEvaluation(detail['evaluation'])['candidateQuestions'] ?? [];
  }
  clarificationStatus(value:string){return ({Queued:'Доработка в очереди',Running:'Идёт доработка и перепроверка',Succeeded:'Доработка завершена',Skipped:'Перепроверка завершена — вакансия пропущена',Rejected:'Перепроверка завершена — отклик отклонён',NotPrepared:'Перепроверка завершена без подготовки документов',NeedsAttention:'После перепроверки требуется внимание',Failed:'Доработка завершилась ошибкой',Interrupted:'Доработка прервана',Cancelled:'Доработка отменена'} as Record<string,string>)[value]??value;}
  async submitApplicationClarification(){
    const detail=this.applicationDetail(),text=this.applicationClarificationText.trim();if(!detail||!text||this.saving())return;
    const id=detail['application'].id,revision=this.clarificationDraftRevision??detail['application'].revision;
    await this.action(async()=>{
      try{await this.api('applications/'+id+'/clarifications','POST',{text,revision,outsideAgeLimit:this.clarificationOutsideAgeLimit});}
      catch(e){await this.loadApplication(id);this.clarificationDraftRevision=this.applicationDetail()?.['application'].revision??null;throw e;}
      this.applicationClarificationText='';this.clarificationOutsideAgeLimit=false;this.clarificationDraftRevision=null;
      this.message.set('Уточнение передано. Этот отклик поставлен на доработку и перепроверку; автоотправка выключена.');
    });
  }
  private applicationRequest:AbortController|null=null;
  private readonly sanitizer=inject(DomSanitizer);
  private applicationResumeId=computed(()=>this.applicationDetail()?.['resume']?.id??'');
  applicationPdf=computed(()=>{const id=this.applicationResumeId();return id?this.sanitizer.bypassSecurityTrustResourceUrl('/api/resumes/versions/'+encodeURIComponent(id)+'/preview'):null;});
  async openApplication(row:Row,changeStatus=false){
    this.applicationStatusEditing=changeStatus;this.applicationNextState='';
    const target=this.recordLink('applications',row['id']);
    if(location.hash===target)await this.loadApplication(row['id']);else location.hash=target;
  }
  async loadApplication(id:string){
    this.applicationRequest?.abort();const request=new AbortController();this.applicationRequest=request;
    const timeout=setTimeout(()=>request.abort('timeout'),12000);
    this.applicationDetailLoading.set(true);this.applicationDetailError.set('');
    const opening=this.applicationDetail()?.['application'].id!==id;
    if(opening){this.applicationDetail.set(null);this.applicationNextState='';this.applicationClarificationText='';this.clarificationOutsideAgeLimit=false;this.clarificationDraftRevision=null;}
    try{
      const detail=await this.api('applications/'+encodeURIComponent(id),'GET',undefined,request.signal);
      if(this.applicationRequest!==request||this.section()!=='applications'||this.recordId()!==id)return;
      const previous=this.applicationDetail()?.['application'];
      if(previous&&previous.revision!==detail.application.revision){
        this.applicationNextState='';
        if(this.applicationStatusEditing)this.message.set('Отклик обновился. Проверьте текущее состояние и выберите новый статус заново.');
      }
      this.applicationDetail.set(detail);this.applicationStatusRevision=detail.application.revision;
      if(opening)setTimeout(()=>{document.querySelector('.application-view')?.scrollIntoView({block:'start'});(document.querySelector(this.applicationStatusEditing?'.application-status-editor select':'#application-view-title') as HTMLElement|null)?.focus();});
      this.data.update(data=>({...data,applications:(data['applications']??[]).map(row=>row['id']===id?{...detail.application,externalDraft:detail.externalDraft}:row)}));
    }catch(e){
      if(this.applicationRequest!==request)return;
      if(request.signal.reason==='timeout')this.applicationDetailError.set('JobFlow не ответил вовремя. Попробуйте обновить ещё раз.');
      else if(!request.signal.aborted)this.applicationDetailError.set((e as Error).message);
    }
    finally{clearTimeout(timeout);if(this.applicationRequest===request){this.applicationDetailLoading.set(false);this.changeDetector.markForCheck();}}
  }
  beginApplicationStatusChange(){this.applicationStatusEditing=true;this.applicationNextState='';this.applicationStatusRevision=this.applicationDetail()!['application'].revision;}
  selectWorkspaceProfile(value: string) {
    this.selectDefaultProfile = !!value;
    if (value !== this.profileId && this.recordId()) this.clearRecord();
    this.profileId = value;
  }
  async saveApplicationStatus(){
    const detail=this.applicationDetail(),state=this.applicationNextState;if(!detail||!state||this.saving())return;
    const id=detail['application'].id,revision=this.applicationStatusRevision;
    await this.action(async()=>{
      try{await this.api('applications/'+id+'/transition','POST',{state,revision});}
      catch(e){await this.loadApplication(id);if((e as Error).message==='Application changed; refresh first.')throw new Error('Отклик изменился. Проверьте текущее состояние и выберите статус заново.');throw e;}
      this.applicationStatusEditing=false;this.applicationNextState='';this.message.set('Статус отклика изменён.');
    });
  }
  applicationStateHint(state:string){return ({Discovered:'Обработка ещё не началась.',Evaluating:'Workflow оценивает соответствие вакансии.',Drafting:'Готовятся письмо и резюме.',ReadyForReview:'Проверьте письмо, PDF и уточнения перед одобрением.',ReadyToSubmit:'Отклик одобрен. Отправка ещё не подтверждена.',Blocked:'Подготовка остановлена. Ниже указана причина.',Rejected:'Отклик отклонён.',Submitting:'Отправка выполняется. Не повторяйте её.',SubmissionUnknown:'Отправка была начата, но подтверждения нет. Сначала проверьте площадку.',Submitted:'Отправка подтверждена.',EmployerResponded:'Получен ответ работодателя.',Closed:'Работа с откликом завершена.',Skipped:'Вакансия пропущена при обработке.'} as Record<string,string>)[state]??'';}
  applicationHistoryText(event:Row){if(event['action']==='StateChanged')return String(event['details']).split('->').map(x=>this.display('state',x.trim())).join(' → ');return event['action']==='BrowserApplicationBlocked'?'Подготовка остановлена: '+event['details']:event['details'];}
  async editLetter(row:Row){this.draftApplication={...row};this.draftLetter=row['coverLetter'];setTimeout(()=>{const editor=document.getElementById('application-letter-editor');editor?.scrollIntoView({block:'start'});editor?.querySelector('textarea')?.focus();});}
  async saveLetter(){if(!this.draftApplication)return;await this.action(async()=>{await this.api('applications/'+this.draftApplication!['id']+'/draft','PUT',{letter:this.draftLetter,revision:this.draftApplication!['revision']});this.draftApplication=null;this.message.set('Черновик сохранён.');});}
  browserDraft:Row|null=null;
  externalReview:Row|null=null;
  externalReviewApplication:Row|null=null;
  async reviewExternalDraft(row:Row){
    this.externalReview=null;this.externalReviewApplication=row;
    await this.action(async()=>{
      const review=await this.api('applications/'+row['id']+'/browser-draft/review');
      if(this.externalReviewApplication===row)this.externalReview=review;
    });
  }
  async takeOverExternalDraft(){
    const row=this.externalReviewApplication,review=this.externalReview;if(!row||!review)return;
    let browser:Row|null=null;
    await this.action(async()=>{
      browser=await this.api('applications/'+row['id']+'/browser-draft/release','POST',{
        revision:row['revision'],fingerprint:review['report'].fingerprint});
      this.externalReview=null;this.externalReviewApplication=null;
      this.message.set('Форма передана вам. Проверьте её и отправьте вручную, затем отметьте отклик отправленным.');
    });
    if(browser){const value=browser as Row;await this.showRemoteBrowser(value['browserId'],'','Внешняя форма отклика',value['tab']);}
  }
  browserPreview:Row|null=null;
  browserContactName='';
  browserFormIndex:number|null=null;
  async openBrowserDraft(row:Row){
    this.browserDraft=row;
    this.browserPreview=null;
    this.browserFormIndex=null;
    await this.command('applications/'+row['id']+'/browser-draft/open');
  }
  async inspectBrowserDraft(){
    if(!this.browserDraft)return;
    const draft=this.browserDraft;
    this.browserPreview=null;
    this.browserFormIndex=null;
    await this.action(async()=>{
      const preview=await this.api('applications/'+draft['id']+'/browser-draft/inspect','POST',{});
      if(this.browserDraft!==draft)return;
      this.browserPreview=preview;
      this.browserContactName=preview.contactName;
      if(preview.forms.length===1)this.browserFormIndex=preview.forms[0].index;
    });
  }
  async fillBrowserDraft(){
    if(!this.browserDraft||!this.browserPreview||this.browserFormIndex===null)return;
    const id=this.browserDraft['id'];
    const {token}=this.browserPreview;
    const formIndex=this.browserFormIndex;
    this.browserPreview=null;
    await this.action(async()=>{
      const result=await this.api('applications/'+id+'/browser-draft/fill','POST',{token,formIndex,contactName:this.browserContactName});
      this.message.set('Заполнено полей: '+result.filled+'. Проверьте форму в браузере.');
    });
  }
  browserFieldLabels:Record<string,string>={name:'Имя',email:'Email',letter:'Сопроводительное письмо',resume:'Резюме (приложите вручную)'};
  async recordManualSubmission(row:Row){if(confirm('Вы уже отправили этот отклик на площадке?'))await this.command('applications/'+row['id']+'/record-manual-submission',{revision:row['revision']});}
  async testSubmit(row:Row){await this.command('applications/'+row['id']+'/test-submit',{idempotencyKey:'test-'+row['id'],revision:row['revision']});}
  async template(kind:string){await this.action(async()=>{const all=await this.api('agent-templates');const value=all.find((x:Row)=>x['taskType']===kind);this.draft={...this.draft,...value,userPromptTemplate:'{{input}}'};});void this.loadModels();}
  platformLogin:Row|null=null;platformLoginBrowserId='';platformLoginProfileId='';platformLoginOpeningId='';
  platformLoginCandidates(row:Row){
    const profiles=this.data()['browser-profiles']??[];
    const accounts=(this.data()['accounts']??[]).filter(x=>x['candidateProfileId']===this.profileId&&x['jobPlatformId']===row['id']);
    const workflows=(this.data()['workflows']??[]).filter(x=>x['candidateProfileId']===this.profileId&&x['executionMode']==='browser'&&this.selectedPlatforms(x).includes(row['id']));
    const ids=new Set([...accounts,...workflows].map(x=>x['browserProfileId']).filter(Boolean));
    return profiles.filter(x=>ids.has(x['id']));
  }
  async authorizePlatform(row:Row){
    if(this.saving()||this.loading())return;
    this.error.set('');this.message.set('');this.platformLogin=row;this.platformLoginProfileId=this.profileId;
    const profiles=this.data()['browser-profiles']??[],candidates=this.platformLoginCandidates(row);
    this.platformLoginBrowserId=candidates.length===1?candidates[0]['id']:candidates.length===0&&profiles.length===1?profiles[0]['id']:'';
    if(this.platformLoginBrowserId)await this.openPlatformLogin();
    else setTimeout(()=>document.getElementById('platform-login-heading')?.focus());
  }
  async openPlatformLogin(){
    const platform=this.platformLogin,id=this.platformLoginBrowserId;
    if(!platform||!id||this.saving())return;
    if(this.platformLoginProfileId!==this.profileId){this.platformLogin=null;return;}
    if(!(this.data()['browser-profiles']??[]).some(x=>x['id']===id)){this.error.set('Браузерный профиль больше недоступен. Обновите список и выберите другой.');return;}
    this.platformLoginOpeningId=platform['id'];
    try{
      await this.showRemoteBrowser(id,platform['url'],platform['name']);
      if(this.remoteBrowserId===id){this.platformLogin=null;if(!this.error())this.message.set('Браузер открыт для входа на '+platform['name']+'.');}
    }finally{this.platformLoginOpeningId='';this.changeDetector.markForCheck();}
  }
  remotePlatformName='';
  remoteBrowserId='';remoteTabs:Row[]=[];remoteTab=0;remoteUrl='';remoteText='';remoteScreen='';remoteBusy=false;remoteHidden=true;remoteImageLoading=false;remoteUpdating=false;remoteError='';
  private remoteGeneration=0;
  private remoteTimer:ReturnType<typeof setInterval>|null=null;
  async showRemoteBrowser(id:string,url='',platformName='',tab=0){
    this.closeRemoteBrowser();
    await this.action(async()=>{
      await this.api('browser-profiles/'+id+'/open','POST',{});
      this.remoteBrowserId=id;this.remoteTab=tab;
      this.remotePlatformName=platformName;this.remoteUrl=url;
      this.changeDetector.markForCheck();
      if(platformName)setTimeout(()=>document.getElementById('remote-browser-heading')?.focus());
      this.remoteTimer=setInterval(()=>{if(!document.hidden&&!this.remoteUpdating)void this.updateRemoteBrowser();},2000);
      if(url)await this.remoteControl('navigate',url);
      else await this.updateRemoteBrowser();
    });
  }
  closeRemoteBrowser(){this.remoteGeneration++;if(this.remoteTimer)clearInterval(this.remoteTimer);this.remoteTimer=null;this.remoteBrowserId='';this.remoteTab=0;this.remoteTabs=[];this.remoteUrl='';this.remoteText='';if(this.remoteScreen)URL.revokeObjectURL(this.remoteScreen);this.remoteScreen='';this.remoteImageLoading=false;this.remoteUpdating=false;this.remoteBusy=false;this.remoteError='';this.remoteQueue=Promise.resolve();this.changeDetector.markForCheck();}
  async updateRemoteBrowser(){
    const id=this.remoteBrowserId,generation=this.remoteGeneration,tab=this.remoteTab;if(!id||this.remoteUpdating)return;
    this.remoteUpdating=true;this.remoteImageLoading=true;this.changeDetector.markForCheck();
    const current=()=>this.remoteGeneration===generation&&this.remoteBrowserId===id&&this.remoteTab===tab;
    try{
      const results=await Promise.allSettled([
        (async()=>{
          const response=await fetch('/api/browser-profiles/'+id+'/screen?tab='+tab+'&t='+Date.now(),{signal:AbortSignal.timeout(12000)});
          if(!response.ok)throw new Error('Не удалось получить экран браузера (HTTP '+response.status+').');
          const frame=await response.blob();
          if(!frame.type.startsWith('image/'))throw new Error('Сервер вернул неверный формат изображения.');
          if(!current())return;
          const old=this.remoteScreen;this.remoteScreen=URL.createObjectURL(frame);if(old)URL.revokeObjectURL(old);
          this.remoteImageLoading=false;this.changeDetector.markForCheck();
        })(),
        (async()=>{
          const result=await this.api('browser-profiles/'+id+'/tabs','GET',undefined,AbortSignal.timeout(12000));
          if(!current())return;
          this.remoteTabs=result.tabs;
          if(result.automating){this.closeRemoteBrowser();this.message.set('Управление браузером передано workflow.');return;}
          if(!this.remoteTabs.some(x=>x['index']===tab))this.remoteTab=this.remoteTabs[0]?.['index']??0;
          const active=this.remoteTabs.find(x=>x['index']===this.remoteTab);
          if(!this.remoteBusy&&document.activeElement?.id!=='remote-browser-address')this.remoteUrl=active?.['url']==='about:blank'?'':active?.['url']??'';
          this.changeDetector.markForCheck();
        })()
      ]);
      if(current()){
        const failure=results.find(result=>result.status==='rejected');
        this.remoteError=failure?.status==='rejected'?'Не удалось обновить браузер. '+(failure.reason?.name==='TimeoutError'?'Сервер не ответил за 12 секунд.':failure.reason?.message??'Проверьте службу поиска.'):'';
      }
    }finally{if(this.remoteGeneration===generation){this.remoteUpdating=false;this.remoteImageLoading=false;this.changeDetector.markForCheck();}}
  }
  private remoteQueue:Promise<void>=Promise.resolve();
  async remoteControl(action:string,value='',x=0,y=0){
    const id=this.remoteBrowserId,tab=this.remoteTab,generation=this.remoteGeneration;
    if(!id)return;
    const work=this.remoteQueue.then(async()=>{
      if(this.remoteBrowserId!==id||this.remoteGeneration!==generation)return;
      this.remoteBusy=true;this.error.set('');this.changeDetector.markForCheck();
      try{await this.api('browser-profiles/'+id+'/control','POST',{action,value,tab,x,y},AbortSignal.timeout(35000));}
      catch(e){if(this.remoteGeneration===generation)this.error.set((e as Error).name==='TimeoutError'?'Браузер не ответил за 35 секунд. Проверьте экран и повторите действие.':(e as Error).message);}
      finally{if(this.remoteGeneration===generation){this.remoteBusy=false;this.changeDetector.markForCheck();void this.updateRemoteBrowser();}}
    });
    this.remoteQueue=work.catch(()=>{});await work;
  }
  async showWorkflowBrowser(row:Row){const first=this.selectedPlatforms(row)[0];const url=(this.data()['platforms']??[]).find(x=>x['id']===first)?.['url']??'';await this.showRemoteBrowser(row['browserProfileId'],url);}
  async sendRemoteText(){const text=this.remoteText;this.remoteText='';if(text)await this.remoteControl('text',text);}
  async remoteClick(event:MouseEvent){const img=event.currentTarget as HTMLImageElement;img.parentElement?.focus();const rect=img.getBoundingClientRect();await this.remoteControl('click','',(event.clientX-rect.left)*img.naturalWidth/rect.width,(event.clientY-rect.top)*img.naturalHeight/rect.height);}
  async remoteKey(event:KeyboardEvent){
    if(event.key.length===1&&!event.ctrlKey&&!event.metaKey&&!event.altKey){event.preventDefault();await this.remoteControl('text',event.key);return;}
    const key=event.ctrlKey&&event.key.toLowerCase()==='a'?'Control+a':event.shiftKey&&event.key==='Tab'?'Shift+Tab':event.key;
    if(['Enter','Tab','Shift+Tab','Backspace','Delete','Escape','ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End','Control+a'].includes(key)){event.preventDefault();await this.remoteControl('key',key);}
  }
  readonly sections = workspaceSections;
  navigationLinks=['dashboard','vacancies','applications','notifications'].map(key => this.sections.find(item => item.key === key)!);
  navigationGroups=[
    {key:'candidate',title:'Профиль и резюме',keys:['profiles','resumes','generated-resumes','dossier-proposals']},
    {key:'search',title:'Поиск вакансий',keys:['workflows','platforms','accounts','browser-profiles','evaluations']},
    {key:'responses',title:'Отклики и ответы',keys:['feedback','interactions']},
    {key:'settings',title:'Службы и подключения',keys:['services','tasks','agents','providers','mcp','integrations']},
    {key:'history',title:'История и журналы',keys:['workflow-runs','application-attempts','runs','executions','logs','imports','imported-applications']}
  ].map(group=>({...group,items:group.keys.map(key=>this.sections.find(item=>item.key===key)!)}));
  openNavigationGroups=signal<string[]>(this.loadNavigationGroups());
  mobileNavigationOpen=false;
  private loadNavigationGroups():string[]{
    try{const saved=JSON.parse(localStorage.getItem('jobflow.navigation.openGroups')??'[]');return Array.isArray(saved)?saved.filter(key=>this.navigationGroups.some(group=>group.key===key)):[];}catch{return [];}
  }
  toggleNavigationGroup(key:string){this.setNavigationGroups(this.openNavigationGroups().includes(key)?this.openNavigationGroups().filter(value=>value!==key):[...this.openNavigationGroups(),key]);}
  private setNavigationGroups(keys:string[]){this.openNavigationGroups.set(keys);try{localStorage.setItem('jobflow.navigation.openGroups',JSON.stringify(keys));}catch{}}
  private revealNavigation(key:string){const group=this.navigationGroups.find(group=>group.keys.includes(key));if(group&&!this.openNavigationGroups().includes(group.key))this.setNavigationGroups([...this.openNavigationGroups(),group.key]);}
  current=computed(()=>this.sections.find(x=>x.key===this.section())!);
  vacancyArchive=signal(false);
  recordId=signal('');
  prepareTarget:Row|null=null;preparationWorkflowId='';preparationProfileId='';preparationOutsideAgeLimit=false;
  recordLink(section:string,id:string){return '#'+section+'?record='+encodeURIComponent(id);}
  private readonly vacanciesById = computed(() => new Map((this.data()['vacancies'] ?? []).map(row => [row['id'], row])));
  preparationVacancy(row:Row){return this.section()==='vacancies'?row:this.vacanciesById().get(row['vacancyId']);}
  canPrepare(row:Row):boolean{
    const vacancy=this.preparationVacancy(row);if(!vacancy||vacancy['isArchived']||vacancy['availability']==='Closed')return false;
    const owner=row['candidateProfileId']||this.profileId;
    const application=owner===this.profileId?this.profileApplications().get(vacancy['id']):(this.data()['applications']??[]).find(x=>x['candidateProfileId']===owner&&x['vacancyId']===vacancy['id']);
    return !application||['Discovered','Evaluating','Drafting','Blocked','Skipped'].includes(application['state']);
  }
  preparationWorkflows(){return (this.data()['workflows']??[]).filter(x=>x['candidateProfileId']===this.preparationProfileId&&x['enabled']&&x['executionMode']==='browser'&&x['applicationStatusFilter']!=='ReadyToSubmit'&&this.selectedPlatforms(x).includes(this.prepareTarget?.['jobPlatformId']));}
  preparationFreshnessWarning():string{
    const workflow=this.preparationWorkflows().find(x=>x['id']===this.preparationWorkflowId),published=this.prepareTarget?.['publishedAt'];
    if(!workflow?.['freshnessDays']||!published)return '';
    const date=Date.parse(/(?:Z|[+-]\d{2}:\d{2})$/i.test(published)?published:published+'Z');
    return Number.isFinite(date)&&date<Date.now()-workflow['freshnessDays']*86400000?'Вакансия старше лимита выбранного workflow ('+workflow['freshnessDays']+' дн.).':'';
  }
  startPreparation(row:Row){
    this.prepareTarget=this.preparationVacancy(row)??null;
    this.preparationProfileId=row['candidateProfileId']||this.profileId;
    this.preparationOutsideAgeLimit=false;
    const app=(this.data()['applications']??[]).find(x=>x['candidateProfileId']===this.preparationProfileId&&x['vacancyId']===this.prepareTarget?.['id']);
    const workflows=this.preparationWorkflows();
    this.preparationWorkflowId=(workflows.find(x=>x['id']===app?.['workflowDefinitionId'])??workflows[0])?.['id']??'';
    this.error.set('');
    setTimeout(()=>document.getElementById('preparation-heading')?.scrollIntoView({block:'start',behavior:'smooth'}));
  }
  async prepareApplication(){
    if(!this.prepareTarget||!this.preparationWorkflowId||this.saving()||(!this.preparationOutsideAgeLimit&&!!this.preparationFreshnessWarning()))return;
    const target=this.prepareTarget;
    await this.action(async()=>{await this.api('vacancies/'+target['id']+'/prepare','POST',{workflowDefinitionId:this.preparationWorkflowId,outsideAgeLimit:this.preparationOutsideAgeLimit});this.prepareTarget=null;this.message.set('Подготовка поставлена в очередь службы поиска. Новый поиск и отправка отклика не выполняются. Результат появится в уведомлениях и откликах.');});
  }
  clearRecord(){this.recordId.set('');location.hash=this.section();}
  readonly runSummaryColumns = ['workflowDefinitionId', 'status', 'checked', 'prepared', 'submitted', 'createdAt'];
  readonly listColumns = computed(() => this.section() === 'workflow-runs' ? this.runSummaryColumns : this.current().columns ?? []);
  readonly runDetailColumns = computed(() => (this.current().columns ?? []).filter(key => !this.runSummaryColumns.includes(key)));
  columnLabel(key:string){return this.section()==='notifications'&&key==='title'?'Уведомление':this.section()==='notifications'&&key==='message'?'Описание':({skipped:'Пропущено',errors:'Технические ошибки',attentionDetails:'Уточнения',error:'Причины и диагностика'} as Record<string,string>)[key]??this.labels[key]??key;}
  notificationKindLabel(kind:string){return ({Information:'Результат',Review:'Нужна проверка',Approval:'Нужно одобрение',Error:'Техническая ошибка',Attention:'Требует внимания'} as Record<string,string>)[kind]??kind;}
  readonly applicationStatusFilter=signal<readonly string[]>([]);
  readonly applicationSort=signal('newest');
  hasApplicationFilter():boolean{return this.section()==='applications'&&this.applicationStatusFilter().length>0;}
  toggleApplicationStatus(status:string, selected:boolean):void{
    this.applicationStatusFilter.update(current=>selected
      ? (current.includes(status)?current:[...current,status])
      : current.filter(value=>value!==status));
  }
  applicationStatusFilterLabel():string{
    const selected=this.applicationStatusFilter();
    if (!selected.length) return 'Все статусы';
    if (selected.length===1) return this.display('state',selected[0]);
    return 'Выбрано: '+selected.length;
  }
  vacancyStatusFilter=signal('all');
  vacancyEvaluationFilter=signal('all');
  hasVacancyFilters():boolean{return this.section()==='vacancies'&&(this.vacancyStatusFilter()!=='all'||this.vacancyEvaluationFilter()!=='all');}
  rows=computed<Row[]>(()=>{
    const section=this.section();
    const record=this.recordId();
    const query=this.query().toLowerCase();
    const rows=(this.data()[section]??[]).filter(row=>{
      if (record && ![row['id'],row['versionId']].includes(record)) return false;
      if (section==='resumes' && row['origin']==='AgentGenerated') return false;
      if (section==='vacancies' && ((!record && !!row['isArchived']!==this.vacancyArchive()) || !this.matchesVacancyFilters(row))) return false;
      const owner=this.ownerProfile(row);
      return (!this.profileId || !owner || owner===this.profileId) && (!query || JSON.stringify(row).toLowerCase().includes(query));
    });
    if (section==='applications' && !record) {
      const statuses=this.applicationStatusFilter();
      const sort=this.applicationSort();
      return rows.filter(row=>!statuses.length||statuses.includes(row['state'])).sort((a,b)=>{
        if (sort!=='newest') {
          const order=this.display('state',a['state']).localeCompare(this.display('state',b['state']),'ru');
          if (order) return sort==='status-asc'?order:-order;
        }
        return String(b['createdAt']).localeCompare(String(a['createdAt']));
      });
    }
    if (section==='evaluations') return rows.map(row=>this.readEvaluation(row));
    if (section==='vacancies') return rows.map((row):Row=>{
      const evaluation=this.latestEvaluation(row);
      return {...row,evaluationDecision:evaluation?this.readEvaluation(evaluation)['evaluationDecision']:'Не оценена',applicationStatus:this.vacancyApplicationStatus(row)};
    });
    return rows;
  });
  private readonly profileApplications = computed(() => new Map(
    (this.data()['applications'] ?? []).filter(row => row['candidateProfileId'] === this.profileId)
      .map(row => [row['vacancyId'], row])
  ));
  private readonly profileEvaluations = computed(() => {
    const latest = new Map<string,Row>();
    for (const row of this.data()['evaluations'] ?? []) {
      if (row['candidateProfileId'] !== this.profileId) continue;
      const old = latest.get(row['vacancyId']);
      if (!old || row['createdAt'] > old['createdAt'] || (row['createdAt'] === old['createdAt'] && row['id'] > old['id']))
        latest.set(row['vacancyId'], row);
    }
    return latest;
  });
  vacancyApplicationStatus(row:Row):string{
    return this.profileApplications().get(row['id'])?.['state'] ?? 'no-application';
  }
  matchesVacancyFilters(row:Row):boolean{
    const status=this.vacancyStatusFilter(),evaluation=this.latestEvaluation(row);
    return (status==='all'||this.vacancyApplicationStatus(row)===status)&&
      (this.vacancyEvaluationFilter()==='all'||(evaluation?this.readEvaluation(evaluation)['evaluationDecision']:'Unevaluated')===this.vacancyEvaluationFilter());
  }
  latestEvaluation(row:Row):Row|undefined{
    return this.profileEvaluations().get(row['id']);
  }
  showEvaluation(row:Row){const assessment=this.latestEvaluation(row);if(assessment)location.hash=this.recordLink('evaluations',assessment['id']);}
  readonly adapterLabels:Record<string,string>={browser:'Браузерный агент', 'search-only':'Импорт / браузерный workflow (старый режим)', 'test-form':'Тестовая форма JobFlow', headhunter:'Поиск через API HeadHunter', greenhouse:'Поиск через API Greenhouse'};
  readonly browserModeLabels:Record<string,string>={inherit:'Общий режим службы',headed:'Видимый Chromium',headless:'Headless (без окна)','new-headless':'Новый headless Chromium'};
  readonly vacancySourceLabels:Record<string,string>={search:'Новый поиск',saved:'Сохранённые необработанные','search-and-saved':'Новый поиск + сохранённые'};
  readonly applicationStatusLabels:Record<string,string>={all:'Все статусы',pending:'Все необработанные','no-application':'Обработка не начата',Discovered:'Найдена',Evaluating:'Оценивается',Rejected:'Отклонена',Drafting:'Подготовка',ReadyForReview:'Ожидает проверки',ReadyToSubmit:'Готова к отправке',Submitting:'Отправляется',SubmissionUnknown:'Результат отправки неизвестен',Submitted:'Отправлена',Blocked:'Требует внимания',EmployerResponded:'Есть ответ',Closed:'Закрыта',Skipped:'Пропущена'};
  readonly evaluationResultLabels:Record<string,string>={all:'Все результаты',Unevaluated:'Без оценки',Proceed:'Подходит',NeedsReview:'Нужна проверка',Reject:'Не подходит'};
  readonly applicationStatusOptions=['Discovered','Evaluating','Drafting','ReadyForReview','ReadyToSubmit','Submitting','Submitted','SubmissionUnknown','Blocked','Rejected','EmployerResponded','Closed','Skipped'];
  readonly vacancyStatusOptions=['all','no-application',...this.applicationStatusOptions];
  readonly vacancyEvaluationOptions=['all','Unevaluated','Proceed','NeedsReview','Reject'];
  fieldOptionLabel(field:Field,option:string):string{
    const labels:Record<string,string>=field.key==='responseLanguage'?{ru:'Русский',en:'English',pl:'Polski',be:'Беларуская',uk:'Українська',de:'Deutsch',es:'Español',fr:'Français'}:field.key==='adapter'?this.adapterLabels:field.key==='browserMode'?this.browserModeLabels:field.key==='vacancySource'?this.vacancySourceLabels:field.key==='applicationStatusFilter'?this.applicationStatusLabels:field.key==='evaluationResultFilter'?this.evaluationResultLabels:{};
    return labels[option]??(option||'По умолчанию');
  }
  adapterHelp(value:string):string{return ({browser:'Агент использует сохранённый браузерный профиль для поиска и откликов. Отправкой управляют настройки workflow.', 'search-only':'Ранее созданный режим без отдельного API. В браузерном workflow площадка всё равно обрабатывается агентом через браузер. Для новой настройки выберите «Браузерный агент».', 'test-form':'Изолированная тестовая площадка для проверки поиска и откликов.', headhunter:'Загрузка вакансий через API HeadHunter. В поле задачи укажите поисковый запрос.', greenhouse:'Загрузка вакансий через API Greenhouse. В поле задачи укажите токен доски вакансий.'} as Record<string,string>)[value]??'';}
  evaluationLists=[{key:'matches',label:'Подтверждённые совпадения'},{key:'missingSkills',label:'Недостающие навыки'},{key:'unverifiedRequirements',label:'Неподтверждённые требования'}];
  private readonly genericEvaluationNotes = /^(?:Confirm salary, location, work format, publication date and profile-specific rules\.|Experience, seniority and mandatory requirements have not been verified\.|Verify experience, languages, education and all source requirements against the selected CV\.|Review additional free-text profile rules:|Mandatory requirements were not supplied as structured data\.)/i;
  diagnosticUrl(row:Row):string{
    return [row['error'],row['message'],row['rawMessage'],row['details'],row['evidence']].filter(value=>typeof value==='string').join(' ')
      .match(/\/api\/browser-diagnostics\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-z-])/i)?.[0]??'';
  }
  cliDiagnosticUrl(row:Row):string{
    return [row['error'],row['message'],row['rawMessage'],row['details'],row['evidence']].filter(value=>typeof value==='string').join(' ')
      .match(/\/api\/cli-diagnostics\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-z-])/i)?.[0]??'';
  }
  readEvaluation(row:Row):Row{
    try{
      const result=JSON.parse(row['resultJson']);
      if(!result||typeof result!=='object'||Array.isArray(result))throw new Error();
      const read=(key:string)=>result[key]??result[key[0].toUpperCase()+key.slice(1)];
      const text=(key:string)=>typeof read(key)==='string'?read(key):'';
      const list=(key:string):string[]=>{
        const values=read(key);
        if (!Array.isArray(values)) return [];
        return values.filter((value):value is string=>typeof value==='string' &&
          (!['clarifications','unverifiedRequirements'].includes(key) || !this.genericEvaluationNotes.test(value.trim())));
      };
      const hasCandidateQuestions=Array.isArray(read('candidateQuestions'));
      const unverified=list('unverifiedRequirements');
      return {...row,evaluationDecision:text('decision'),evaluationRationale:text('rationale')||'Причина не указана.',evaluationConfidence:text('confidence'),
        matches:list('matches'),missingSkills:list('missingSkills'),
        unverifiedRequirements:hasCandidateQuestions?unverified:[],
        candidateQuestions:list('candidateQuestions'),
        historicalEvaluationNotes:hasCandidateQuestions?[]:[...new Set([...list('clarifications'),...unverified])]};
    }catch{return {...row,evaluationDecision:'Ошибка данных',evaluationRationale:'Не удалось прочитать сохранённый результат оценки.',evaluationConfidence:''};}
  }
  ownerProfile(row:Row):string|undefined{
    if(row['candidateProfileId'])return row['candidateProfileId'];
    const related:Record<string,string>={integrationId:'integrations',jobApplicationId:'applications',scheduledTaskId:'tasks',agentProfileId:'agents'};
    for(const [key,source] of Object.entries(related))if(row[key]){
      const parent=(this.data()[source]??[]).find(x=>x['id']===row[key]);
      return parent?.['candidateProfileId']??'unknown-owner';
    }
    return undefined;
  }
  readonly gmailEndpoint='https://gmail.googleapis.com/gmail/v1/users/me/messages';
  readonly mailInstructions='Проверяй письма об откликах, отказах, приглашениях и вопросах работодателей. Сопоставляй их с сохранёнными откликами только по подтверждённым данным. Неуверенные совпадения оставляй для проверки. Не отправляй письма и не выдумывай факты.';
  fieldChanged(field:Field){
    if(this.section()==='workflows'&&field.key==='applicationStatusFilter'&&this.draft['applicationStatusFilter']!=='ReadyToSubmit')
      this.draft['submitExternalApplications']=false;
    if(this.section()==='workflows'&&field.key==='applicationStatusFilter'&&this.draft['applicationStatusFilter']==='ReadyToSubmit'){
      this.draft['executionMode']='browser';
      this.draft['vacancySource']='saved';
      this.draft['autoSubmit']=true;
      this.draft['evaluationResultFilter']='all';
      this.draft['checkMail']=false;
    }
    if(this.section()==='agents'&&field.key==='taskType'&&!['BrowserAutomation','Resume','Dossier','Evaluation','CoverLetter','MailAnalysis'].includes(this.draft['taskType']))this.draft['useTaskModelPresets']=false;
    if(this.section()==='workflows'&&field.key==='executionMode'&&this.draft['executionMode']!=='browser')this.draft['vacancySource']='search';
    if(this.section()==='agents'&&field.key==='modelProviderId'||this.section()==='providers'&&field.key==='kind')void this.loadModels();
    if(this.section()==='integrations'&&field.key==='kind'&&this.draft['kind']==='hh-chats')
      this.draft['endpoint']='https://api.hh.ru/common/chats';
    if(this.section()==='integrations'&&field.key==='kind'&&this.draft['kind']==='gmail'){
      this.draft['endpoint']=this.gmailEndpoint;
      if(!this.draft['id'])this.draft['jobPlatformId']=null;
      if(!this.draft['instructions']||this.draft['instructions']==='Используй только предоставленное досье. Не добавляй отсутствующий опыт. Работай только с актуальными открытыми вакансиями.')this.draft['instructions']=this.mailInstructions;
    }
  }
  formFields(){return (this.current().fields??[])
    .filter(field=>field.key!=='submitExternalApplications'||this.draft['applicationStatusFilter']==='ReadyToSubmit')
    .filter(field=>this.section()!=='agents'||!this.draft['useTaskModelPresets']||!['model','reasoning'].includes(field.key))
    .filter(field=>this.section()!=='integrations'||this.draft['kind']!=='gmail'||!['jobPlatformId','endpoint','mappingJson','conflictPolicy'].includes(field.key))
    .filter(field=>(field.key!=='mailAgentId'||['gmail','hh-chats','feedback-json'].includes(this.draft['kind']))&&
    (!['browserProfileId','browserAgentId','autoSubmit','checkMail','maxBrowserSteps'].includes(field.key)||this.section()!=='workflows'||this.draft['executionMode']==='browser'))
    .filter(field=>this.section()!=='workflows'||field.key!=='resumeVersionId'||this.draft['executionMode']==='prepare'||this.draft['resumeMode']==='fixed')
    .filter(field=>this.section()!=='workflows'||!['resumeMode','resumeAgentId'].includes(field.key)||this.draft['executionMode']==='browser')
    .filter(field=>this.section()!=='workflows'||field.key!=='vacancySource'||this.draft['executionMode']==='browser')
    .filter(field=>this.section()!=='workflows'||field.key!=='maxVacanciesPerPlatform'||this.draft['vacancySource']!=='saved'||this.draft['executionMode']!=='browser')
    .map(field=>this.section()==='tasks'&&field.key==='timeoutSeconds'?{...field,max:this.draft['kind']==='Workflow'?21600:900}:this.section()==='workflows'&&field.key==='resumeAgentId'?{...field,required:this.draft['resumeMode']!=='fixed'}:this.section()==='workflows'&&this.draft['executionMode']==='browser'&&field.key==='evaluationAgentId'?{...field,label:'AI-оценка подходящих вакансий',required:true}:field);}
  fieldOptions(field:Field){
    if(field.key==='reasoning'&&this.selectedModel()){
      const supported=this.selectedModel()!.supportedReasoningEfforts;
      return [...new Set(['',...(field.options??[]).filter(x=>supported.includes(x)),...(this.draft['reasoning']?[this.draft['reasoning']]:[])])];
    }
    return (field.options??[]).filter(x=>x!=='Workflow'||!!this.draft['id']);
  }
  labels:Record<string,string>={evaluationDecision:'Решение',evaluationRationale:'Причина',evaluationConfidence:'Уверенность',responseStatus:'Ответ работодателя',responseStatusAt:'Обновлено источником',category:'Категория',analyzed:'Разобрано',jobPlatformIds:'Площадки',isPrimary:'Основное',jobPlatformId:'Площадка',browserProfileId:'Браузерный профиль',externalResumeId:'ID резюме',workflowDefinitionId:'Workflow',stage:'Этап',discovered:'Найдено',checked:'Проверено',prepared:'Подготовлено',rejected:'Отклонено',transport:'Транспорт',hasSecrets:'Секреты',lastStatus:'Последняя проверка',importConfirmed:'Импорт подтверждён',integrationId:'Интеграция',rowCount:'Строк',added:'Добавлено',duplicates:'Дубликаты',conflicts:'Конфликты',sourceState:'Статус источника',sourceKey:'ID источника',subject:'Тема',receivedAt:'Получено',needsAction:'Требует внимания',jobApplicationId:'Отклик',outcome:'Результат',evidence:'Подтверждение',name:'Название',email:'Email',skills:'Навыки',geography:'География',version:'Версия',originalName:'Файл',createdAt:'Создано',
    url:'URL',adapter:'Способ работы',browserMode:'Режим браузера',title:'Должность',employer:'Работодатель',externalId:'Внешний ID',vacancyId:'Вакансия',candidateProfileId:'Профиль',state:'Состояние',
    revision:'Ревизия',taskType:'Этап',enabled:'Включён',kind:'Тип',model:'Модель',busy:'Открыт',intervalMinutes:'Интервал, мин',nextRunAt:'Следующий запуск',
    role:'Служба',heartbeatAt:'Последний сигнал',processId:'Процесс',readAt:'Прочитано',confirmedAt:'Подтверждено',sourceResumeVersionId:'Исходный PDF',agentExecutionId:'Вызов агента',resumeVersionId:'Версия PDF',details:'Подробности',
    status:'Статус',detail:'Возможности',scheduledTaskId:'Задание',message:'Результат',finishedAt:'Завершено',agentProfileId:'Агент',
    executionMode:'Режим',vacancySource:'Источник вакансий',applicationStatus:'Статус обработки',autoSubmit:'Автоотправка',submitted:'Отправлено',needsAttention:'Требует внимания',
    operation:'Операция',reasoning:'Reasoning',responseLanguage:'Язык объяснений',configurationVersion:'Версия',error:'Ошибка',entityType:'Сущность',action:'Действие',entityId:'ID'};
  private unavailable = signal<string[]>([]);
  readonly loadWarning = computed(() => {
    const keys = this.unavailable();
    const titles = this.sections.filter(section => keys.includes(section.key) &&
      (!this.journals.has(section.key) || section.key === this.section())).map(section => section.title);
    return titles.length ? 'Не обновлены разделы: ' + titles.join(', ') + '. Попробуйте обновить ещё раз.' : '';
  });
  private applyCollections(keys: string[], snapshot: Awaited<ReturnType<typeof readCollections>>) {
    this.data.update(current => ({...current, ...snapshot.records}));
    this.unavailable.update(previous => [...previous.filter(key => !keys.includes(key)), ...snapshot.failed]);
  }
  private refreshRequest?: AbortController;
  private pollRequest?: AbortController;
  private pollTimer?: ReturnType<typeof setInterval>;
  private destroyed = false;
  private readonly journals = new Set(['logs', 'executions', 'runs', 'imports', 'imported-applications', 'application-attempts']);

  ngOnInit() {
    this.applyHashRoute();
    void this.refresh();
    this.pollTimer = setInterval(() => void this.pollWorkspace(), 15000);
  }

  ngOnDestroy() {
    this.destroyed = true;
    clearInterval(this.pollTimer);
    this.refreshRequest?.abort();
    this.pollRequest?.abort();
    this.applicationRequest?.abort();
    this.modelsRequest?.abort();
    this.closeRemoteBrowser();
  }

  private async pollWorkspace() {
    if (document.hidden || this.loading() || this.saving() || this.pollRequest) return;
    const request = new AbortController();
    this.pollRequest = request;
    const section = this.section();
    const keys = ['services', 'notifications'];
    if (section === 'applications' || section === 'dashboard') keys.push('applications');
    if (section === 'workflow-runs' || section === 'dashboard') keys.push('workflow-runs');
    try {
      const snapshot = await readCollections(keys, request.signal);
      if (request.signal.aborted || this.saving() || this.section() !== section) return;
      this.applyCollections(keys, snapshot);
      if (section === 'applications' && this.recordId() && !this.applicationDetailLoading() && !this.applicationDetailError())
        await this.loadApplication(this.recordId());
    } finally {
      if (this.pollRequest === request) this.pollRequest = undefined;
    }
  }

  openApplicationQueue(state: string) {
    this.applicationStatusFilter.set(state ? [state] : []);
    this.navigate('applications');
  }
  @HostListener('window:hashchange') onHashChange(){this.applyHashRoute();if(this.recordId()||this.journals.has(this.section()))void this.refresh();if(!this.recordId()){this.applicationRequest?.abort();this.applicationDetail.set(null);this.applicationStatusEditing=false;this.applicationNextState='';}}
  private applyHashRoute(){const [key,params='']=location.hash.slice(1).split('?');if(!this.sections.some(x=>x.key===key)){this.navigate('dashboard',false);return;}const id=new URLSearchParams(params).get('record')??'';if(key!==this.section())this.navigate(key,false);this.recordId.set(id);this.revealNavigation(key);if(id){this.query.set('');this.vacancyStatusFilter.set('all');this.vacancyEvaluationFilter.set('all');}}
  navigate(key:string,updateHash=true){this.applicationRequest?.abort();this.applicationDetail.set(null);this.applicationStatusEditing=false;this.applicationNextState='';this.recordId.set('');this.prepareTarget=null;this.googleConnection=null;this.revealNavigation(key);this.mobileNavigationOpen=false;this.addingPlatform=false;this.section.set(key);if(updateHash)location.hash=key;this.query.set('');this.editing.set(false);this.result.set('');this.activeAgent=null;this.draftApplication=null;this.browserDraft=null;this.browserPreview=null;this.externalReview=null;this.externalReviewApplication=null;this.feedbackEvent=null;this.importIntegration=null;this.sheetIntegration=null;this.sheetPreview=null;this.secretConnection=null;this.secretJson='{}';this.error.set('');this.message.set('');}
  readonly api = requestApi;

  async refresh() {
    if (this.destroyed) return;
    this.refreshRequest?.abort();
    this.pollRequest?.abort();
    const request = new AbortController();
    this.refreshRequest = request;
    this.loading.set(true);
    const sections = this.sections.filter(section => section.columns && (!this.journals.has(section.key) || section.key === this.section()));
    try {
      const keys = sections.map(section => section.key);
      const snapshot = await readCollections(keys, request.signal);
      if (request.signal.aborted) return;
      this.applyCollections(keys, snapshot);
      const profiles = snapshot.records['profiles'];
      if (profiles && this.selectDefaultProfile && !profiles.some(profile => profile['id'] === this.profileId))
        this.profileId = profiles[0]?.['id'] ?? '';
      const focused = (this.data()[this.section()] ?? []).find(row => row['id'] === this.recordId());
      const owner = focused ? this.ownerProfile(focused) : null;
      if (owner && owner !== 'unknown-owner' && owner !== this.profileId) this.profileId = owner;
      if (this.section() === 'applications' && this.recordId()) await this.loadApplication(this.recordId());
    } finally {
      if (this.refreshRequest === request) this.loading.set(false);
    }
  }
  create(){this.addingPlatform=false;this.draft={mappingJson:'{"externalId":"Id","title":"Title","employer":"Employer","url":"Url","description":"Description","applicationState":"Status"}',conflictPolicy:'keep-local',transport:'stdio',argumentsJson:'[]',permissionsJson:'[]',toolBindingsJson:'[]',rulesJson:'{}',candidateProfileId:this.profileId,adapter:'browser',browserMode:'inherit',kind:this.section()==='tasks'?'HealthCheck':this.section()==='integrations'?'csv':'codex-cli',timeoutSeconds:120,
    executionMode:'browser',vacancySource:'search',applicationStatusFilter:'pending',evaluationResultFilter:'all',autoSubmit:false,checkMail:false,maxApplicationsPerRun:10,maxVacancyChecksPerRun:30,maxVacanciesPerPlatform:100,maxBrowserSteps:80,resumeMode:'tailor',freshnessDays:7,instructions:'Используй только предоставленное досье. Не добавляй отсутствующий опыт. Работай только с актуальными открытыми вакансиями.',
    intervalMinutes:60,scheduleKind:'interval',localTime:'09:00',daysOfWeek:'1,2,3,4,5',maxRetries:2,timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,enabled:false,taskType:'Evaluation',userPromptTemplate:'{{input}}',
    systemPrompt:'Use only verified candidate facts. Clearly identify uncertainty.',model:'',reasoning:'',responseLanguage:'ru',useTaskModelPresets:true,
    outputSchema:'{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}'};this.editing.set(true);this.error.set('');void this.loadModels();}
  edit(row:Row){this.addingPlatform=false;this.draft={...row};this.editing.set(true);void this.loadModels();}
  copy(row:Row){this.addingPlatform=false;this.draft={...row,name:row['name']+' — копия',enabled:false};delete this.draft['id'];this.editing.set(true);void this.loadModels();}
  options(field:Field){if(field.source==='browser-agents')return (this.data()['agents']??[]).filter(x=>x['taskType']==='BrowserAutomation'&&x['candidateProfileId']===this.draft['candidateProfileId']);if(field.source==='mail-agents')return (this.data()['agents']??[]).filter(x=>x['taskType']==='MailAnalysis'&&x['candidateProfileId']===this.draft['candidateProfileId']);let list=field.source?this.data()[field.source]??[]:[];if(field.source==='resumes')list=list.filter(x=>x['candidateProfileId']===this.draft['candidateProfileId']&&x['origin']!=='AgentGenerated');if(field.source==='vacancies')list=list.filter(x=>!x['isArchived']);return list;}
  optionId(field:Field,row:Row){return field.source==='resumes'?row['versionId']:row['id'];}
  addingPlatform=false;platformNameInput='';platformUrlInput='';
  openPlatformEditor(){this.addingPlatform=true;this.platformNameInput='';this.platformUrlInput='';}
  async saveInlinePlatform(){
    if(this.saving())return;
    const name=this.platformNameInput.trim(),url=this.platformUrlInput.trim();
    if(!name){this.error.set('Укажите название площадки.');return;}
    try{if(!['http:','https:'].includes(new URL(url).protocol))throw new Error();}
    catch{this.error.set('Укажите полный адрес площадки, например https://hh.ru.');return;}
    await this.action(async()=>{
      const platform=await this.api('platforms','POST',{name,url,adapter:'browser'});
      this.data.update(data=>({...data,platforms:[...(data['platforms']??[]),platform]}));
      this.draft['jobPlatformId']=platform.id;this.addingPlatform=false;
      this.message.set('Площадка добавлена и выбрана. Сохраните аккаунт.');
    });
  }
  optionName(row:Row){return(row['name']??row['title']??row['id'])+(row['version']?' · v'+row['version']:'');}
  platformName=(id:string)=>this.display('jobPlatformId',id);
  selectedPlatforms(row:Row):string[]{
    return row['jobPlatformIds']?.length ? row['jobPlatformIds'] : row['jobPlatformId'] ? [row['jobPlatformId']] : [];
  }
  togglePlatform(id:string,checked:boolean){
    const ids=this.selectedPlatforms(this.draft);
    this.draft['jobPlatformIds']=checked?[...ids,id]:ids.filter(value=>value!==id);
    this.draft['jobPlatformId']=this.draft['jobPlatformIds'][0];
  }
  selectAllPlatforms(){
    this.draft['jobPlatformIds']=(this.data()['platforms']??[]).map(row=>row['id']);
    this.draft['jobPlatformId']=this.draft['jobPlatformIds'][0];
  }
  canSaveEditor(valid: boolean | null): boolean {
    if (!valid || this.saving()) return false;
    switch (this.section()) {
      case 'agents': return !this.reasoningWarning();
      case 'accounts': return !this.addingPlatform;
      case 'workflows': return this.selectedPlatforms(this.draft).length > 0;
      default: return true;
    }
  }
  async save(){if(this.saving())return;if(this.section()==='accounts'&&this.addingPlatform){await this.saveInlinePlatform();return;}await this.action(async()=>{if(this.section()==='integrations'&&this.draft['kind']==='gmail'&&!this.draft['endpoint'])this.draft['endpoint']=this.gmailEndpoint;if(this.section()==='workflows'&&this.draft['executionMode']!=='browser'){this.draft['autoSubmit']=false;this.draft['browserProfileId']=null;this.draft['browserAgentId']=null;this.draft['resumeMode']='fixed';this.draft['resumeAgentId']=null;}if(this.section()==='integrations'&&!['gmail','hh-chats','feedback-json'].includes(this.draft['kind']))this.draft['mailAgentId']=null;for(const key of ['evaluationAgentId','letterAgentId','mailAgentId','jobPlatformId','integrationId','browserProfileId','browserAgentId','resumeAgentId'])if(this.draft[key]==='')this.draft[key]=null;await this.api(this.section()+(this.draft['id']?'/'+this.draft['id']:''),this.draft['id']?'PUT':'POST',this.draft);this.editing.set(false);this.message.set('Сохранено.');});}
  chooseFile(event:Event){this.file=(event.target as HTMLInputElement).files?.[0]??null;}
  uniqueResumes(){return [...new Map((this.data()['resumes']??[]).filter(x=>x['candidateProfileId']===this.profileId).map(x=>[x['id'],x])).values()];}
  async upload(){if(!this.file||!this.profileId){this.error.set('Выберите профиль и PDF.');return;}await this.action(async()=>{
    const form=new FormData();form.append('profileId',this.profileId);form.append('file',this.file!);if(this.resumeId)form.append('resumeId',this.resumeId);
    await this.api('resumes','POST',form);this.message.set('Версия резюме загружена.');this.file=null;});}
  async action(task:()=>Promise<void>){if(this.saving())return false;this.pollRequest?.abort();this.saving.set(true);this.error.set('');this.message.set('');try{await task();await this.refresh();return true;}
    catch(e){this.error.set((e as Error).message);return false;}finally{this.saving.set(false);}}
  async command(path:string,body:unknown={}){
    return this.action(async()=>{
      const value=await this.api(path,'POST',body);
      this.result.set(JSON.stringify(value,null,2));
      const isRun=path.endsWith('/run');
      if(isRun&&path.startsWith('workflows/'))
        this.message.set(value?.alreadyQueued?'Следующий прогон уже в очереди.':'Прогон поставлен в очередь и начнётся, когда освободится workflow.');
      else if(isRun&&path.startsWith('tasks/'))
        this.message.set('Задание поставлено в очередь. Проверьте «Запуски заданий»; Worker должен быть запущен.');
      else this.message.set('Действие выполнено.');
    });
  }
  async evaluate(row:Row,reevaluate=false){
    if(this.saving())return;
    if(!this.profileId){this.error.set('Сначала выберите профиль.');return;}
    if(this.latestEvaluation(row)&&!reevaluate){this.showEvaluation(row);return;}
    if(reevaluate&&!confirm('Повторно оценить вакансию «'+row['title']+'» для выбранного профиля? Будет сохранена новая оценка; предыдущие останутся в истории.'))return;
    await this.action(async()=>{const value=await this.api('vacancies/'+row['id']+'/evaluate','POST',{profileId:this.profileId,reevaluate});this.result.set(JSON.stringify(value,null,2));this.message.set(reevaluate?'Новая оценка сохранена. Предыдущие оценки доступны в истории.':'Оценка сохранена.');});
  }
  async removeVacancy(row:Row){
    if(this.saving()||!confirm('Удалить вакансию «'+row['title']+'» из поиска для всех профилей? Она будет перемещена в архив; оценки, отклики и PDF сохранятся.'))return;
    await this.action(async()=>{await this.api('vacancies/'+row['id'],'DELETE');this.message.set('Вакансия удалена из поиска. Восстановить её можно в архиве.');});
  }
  async restoreVacancy(row:Row){await this.action(async()=>{await this.api('vacancies/'+row['id']+'/restore','POST',{});this.message.set('Вакансия восстановлена в поиске.');});}
  async execute(){if(this.activeAgent)await this.command('agents/'+this.activeAgent['id']+'/execute',{input:this.agentInput});}
  vacancyUrl(row:Row){
    const url=(this.data()['vacancies']??[]).find(v=>v['id']===row['vacancyId'])?.['url'];
    return typeof url==='string'&&['https://','http://'].some(prefix=>url.toLowerCase().startsWith(prefix))?url:null;
  }
  transitions(row:Row){const allowed:Record<string,string[]>={Discovered:['Evaluating'],Evaluating:['Rejected','Drafting','Blocked','Closed','Skipped'],Drafting:['ReadyForReview','Blocked','Closed','Skipped'],
    ReadyForReview:['Drafting','ReadyToSubmit','Rejected'],ReadyToSubmit:['ReadyForReview','Blocked'],Blocked:['Evaluating','Drafting','Closed'],Skipped:['Evaluating'],Submitted:['Closed'],EmployerResponded:['Closed'],SubmissionUnknown:['Closed']};return allowed[row['state']]??[];}
  display(key:string,value:any):string{if(key==='state')return this.applicationStatusLabels[value]??String(value??'—');if(key==='status')return runStatusLabels[value]??String(value??'—');if(key==='adapter')return this.adapterLabels[value]??String(value??'—');if(key==='evaluationDecision')return this.evaluationResultLabels[value]??(value||'Решение не указано');if(key==='evaluationConfidence')return ({Low:'Низкая',Medium:'Средняя',High:'Высокая'} as Record<string,string>)[value]??(value||'Не указана');if(['sourceResumeVersionId','resumeVersionId'].includes(key)){const source=(this.data()['resumes']??[]).find(x=>x['versionId']===value);if(source)return source['originalName']+' · v'+source['version'];}const map:Record<string,string>={candidateProfileId:'profiles',vacancyId:'vacancies',scheduledTaskId:'tasks',agentProfileId:'agents',integrationId:'integrations',workflowDefinitionId:'workflows',jobPlatformId:'platforms',browserProfileId:'browser-profiles',jobApplicationId:'applications'};
    if(map[key]){const row=(this.data()[map[key]]??[]).find(x=>x['id']===value);if(row)return row['name']??row['title']??(row['vacancyId']?this.display('vacancyId',row['vacancyId']):value);}
    if(key==='role')return value==='search'?'Поиск':'Мониторинг';if(typeof value==='boolean')return value?'Да':'Нет';if(key.endsWith('At')&&value)return workspaceTime(String(value));return String(value??'—');}
  async removeBrowser(row:Row){if(!confirm('Удалить профиль «'+row['name']+'» и сохранённую авторизацию?'))return;
    await this.action(async()=>{await this.api('browser-profiles/'+row['id']+'?confirm=true','DELETE');});}
}
