import { Component, signal, computed, OnInit, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
type Row = Record<string, any>;
type Field = {key:string;label:string;type?:string;options?:string[];source?:string;required?:boolean};
type Section = {key:string;title:string;description:string;columns?:string[];fields?:Field[]};
@Component({selector:'app-root',imports:[CommonModule,FormsModule],templateUrl:'./app.html',styleUrl:'./app.css'})
export class App implements OnInit {
  section=signal('dashboard'); data=signal<Record<string,Row[]>>({}); status=signal<Row>({});
  loading=signal(false); saving=signal(false); error=signal(''); message=signal(''); result=signal(''); editing=signal(false); query=signal('');
  private selectedProfile=signal(''); get profileId(){return this.selectedProfile();} set profileId(value:string){this.selectedProfile.set(value);this.resumeId='';this.activeAgent=null;this.draftApplication=null;this.browserDraft=null;this.browserPreview=null;this.feedbackEvent=null;this.importIntegration=null;this.sheetIntegration=null;this.sheetPreview=null;this.secretConnection=null;this.secretJson='{}';this.editing.set(false);} draft:Row={}; file:File|null=null; resumeId=''; agentInput=''; activeAgent:Row|null=null;
  feedbackEvent:Row|null=null;feedbackApplicationId='';
  feedbackApplications(){return (this.data()['applications']??[]).filter(x=>x['candidateProfileId']===this.feedbackEvent?.['candidateProfileId']&&['Submitted','EmployerResponded'].includes(x['state']));}
  async linkFeedback(){if(!this.feedbackEvent)return;await this.command('feedback/'+this.feedbackEvent['id']+'/link',{applicationId:this.feedbackApplicationId});if(!this.error())this.feedbackEvent=null;}
  importIntegration:Row|null=null; csvText='';
  async selectCsv(event:Event){const file=(event.target as HTMLInputElement).files?.[0];if(file){if(file.size>5242880){this.error.set('Максимум 5 МБ.');return;}this.csvText=await file.text();}}
  async previewImport(){if(this.importIntegration)await this.command('integrations/'+this.importIntegration['id']+'/preview',{csv:this.csvText});}
  sheetIntegration:Row|null=null;
  sheetTab='JobFlow Export';
  sheetPreview:{tab:string;digest:string;existing:string[][];rows:string[][]}|null=null;
  openSheetExport(row:Row){this.sheetIntegration=row;this.sheetTab='JobFlow Export';this.sheetPreview=null;}
  async previewSheet(){
    if(!this.sheetIntegration)return;
    this.sheetPreview=null;
    await this.action(async()=>{this.sheetPreview=await this.api('integrations/'+this.sheetIntegration!['id']+'/sheets/preview','POST',{tab:this.sheetTab});});
  }
  async confirmSheet(){
    if(!this.sheetIntegration||!this.sheetPreview||this.sheetPreview.tab!==this.sheetTab)return;
    const {tab,digest}=this.sheetPreview;
    this.sheetPreview=null;
    await this.command('integrations/'+this.sheetIntegration['id']+'/sheets/confirm',{tab,digest});
  }
  async connectGoogle(row:Row){
    await this.action(async()=>{
      const {authorizationUrl}=await this.api('integrations/'+row['id']+'/google/connect','POST',{});
      location.assign(authorizationUrl);
    });
  }
  secretConnection:Row|null=null; secretJson='{}';
  async saveSecrets(){if(!this.secretConnection)return;await this.action(async()=>{await this.api((this.secretConnection!['kind']?'integrations/':'mcp/')+this.secretConnection!['id']+'/secrets','POST',JSON.parse(this.secretJson));this.secretJson='{}';this.secretConnection=null;this.message.set('Секреты сохранены отдельно от конфигурации.');});}
  draftApplication:Row|null=null; draftLetter='';
  async editLetter(row:Row){this.draftApplication={...row};this.draftLetter=row['coverLetter'];}
  async saveLetter(){if(!this.draftApplication)return;await this.action(async()=>{await this.api('applications/'+this.draftApplication!['id']+'/draft','PUT',{letter:this.draftLetter,revision:this.draftApplication!['revision']});this.draftApplication=null;this.message.set('Черновик сохранён.');});}
  browserDraft:Row|null=null;
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
  async template(kind:string){await this.action(async()=>{const all=await this.api('agent-templates');const value=all.find((x:Row)=>x['taskType']===kind);this.draft={...this.draft,...value,userPromptTemplate:'{{input}}'};});}
  sections:Section[]=[
    {key:'workflows',title:'Workflow',description:'Поиск → оценка → письмо → ручная проверка. Без AI используются правила и письмо из фактов профиля. Выполняет Worker.',columns:['name','jobPlatformIds','enabled','revision'],fields:[
      {key:'name',label:'Название',required:true},{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},
      {key:'resumeVersionId',label:'Версия резюме',type:'select',source:'resumes',required:true},
      {key:'evaluationAgentId',label:'AI-оценка (необязательно)',type:'select',source:'agents'},{key:'letterAgentId',label:'AI-письмо (необязательно)',type:'select',source:'agents'},
      {key:'rulesJson',label:'Правила JSON: requiredSkills, locations, workModes, minimumSalary, currency, maxAgeDays, levels',type:'textarea',required:true},
      {key:'enabled',label:'Разрешить выполнение workflow',type:'checkbox'}]},
    {key:'workflow-runs',title:'История workflow',description:'Сохранённые этапы и результаты обработки.',columns:['workflowDefinitionId','status','stage','prepared','rejected','error','createdAt']},
    {key:'evaluations',title:'Оценки вакансий',description:'Обоснования и неопределённости; оценка не заменяет проверку человеком.',columns:['candidateProfileId','vacancyId','createdAt']},
    {key:'dashboard',title:'Обзор',description:'Ваш поиск работы — в одном рабочем пространстве.'},
    {key:'profiles',title:'Профили кандидата',description:'Опыт, предпочтения и правила отдельного профиля.',columns:['name','email','skills','geography'],fields:[
      {key:'name',label:'Название профиля',required:true},{key:'email',label:'Email',type:'email'},
      {key:'experience',label:'Подтверждённый опыт',type:'textarea'},{key:'skills',label:'Навыки через запятую'},
      {key:'education',label:'Образование',type:'textarea'},{key:'languages',label:'Языки'},{key:'preferredRoles',label:'Предпочитаемые роли'},
      {key:'geography',label:'География'},{key:'salary',label:'Зарплата и валюта'},{key:'relocation',label:'Релокация'},
      {key:'excludedEmployers',label:'Исключённые работодатели через запятую'},{key:'rules',label:'Правила поиска',type:'textarea'}]},
    {key:'resumes',title:'Резюме',description:'Локальные PDF и версии. Каждый отклик привязан к конкретной версии.',columns:['name','isPrimary','version','originalName','createdAt']},
    {key:'accounts',title:'Аккаунты площадок',description:'Связи профиля кандидата, площадки и браузера. Внешний ID резюме задаётся вручную; его актуальность не подтверждена площадкой.',columns:['candidateProfileId','jobPlatformId','browserProfileId','externalResumeId'],fields:[{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},{key:'jobPlatformId',label:'Площадка',type:'select',source:'platforms',required:true},{key:'browserProfileId',label:'Браузерный профиль',type:'select',source:'browser-profiles'},{key:'externalResumeId',label:'ID резюме на площадке'}]},
    {key:'platforms',title:'Площадки',description:'Добавление URL не включает автоматическую отправку.',columns:['name','url','adapter'],fields:[
      {key:'name',label:'Название',required:true},{key:'url',label:'URL',type:'url',required:true},
      {key:'adapter',label:'Адаптер',type:'select',options:['search-only','test-form','headhunter','greenhouse']},{key:'geography',label:'География'},{key:'searchFilters',label:'Запрос HeadHunter или токен доски Greenhouse',type:'textarea'}]},
    {key:'vacancies',title:'Вакансии',description:'Ручной импорт и предварительная оценка для выбранного профиля.',columns:['title','employer','externalId','createdAt'],fields:[
      {key:'jobPlatformId',label:'Площадка',type:'select',source:'platforms',required:true},{key:'externalId',label:'ID на площадке',required:true},
      {key:'title',label:'Должность',required:true},{key:'employer',label:'Работодатель',required:true},{key:'url',label:'Ссылка',type:'url'},
      {key:'description',label:'Описание и требования',type:'textarea',required:true},
      {key:'requiredSkills',label:'Обязательные навыки через запятую'},{key:'location',label:'Локация'},{key:'workMode',label:'Формат работы'},{key:'level',label:'Уровень'},
      {key:'salaryMax',label:'Верхняя граница зарплаты',type:'number'},{key:'currency',label:'Валюта'},{key:'publishedAt',label:'Опубликована (ISO дата и время)'}]},
    {key:'applications',title:'Отклики',description:'Подготовка и проверка. Реальная отправка отключена.',columns:['vacancyId','candidateProfileId','state','responseStatus','responseStatusAt','revision'],fields:[
      {key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},{key:'vacancyId',label:'Вакансия',type:'select',source:'vacancies',required:true},
      {key:'resumeVersionId',label:'Версия резюме',type:'select',source:'resumes',required:true},{key:'coverLetter',label:'Сопроводительное письмо',type:'textarea'}]},
    {key:'agents',title:'Агенты',description:'Версии конфигурации сохраняются. Включение разрешает вызов провайдера.',columns:['name','taskType','version','enabled'],fields:[
      {key:'name',label:'Название',required:true},{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},
      {key:'modelProviderId',label:'Провайдер',type:'select',source:'providers',required:true},
      {key:'taskType',label:'Этап',type:'select',options:['Discovery','Evaluation','CoverLetter','Preparation','Submission','Monitoring','MailAnalysis','Custom']},
      {key:'description',label:'Описание'},{key:'model',label:'Модель (пусто — модель провайдера)'},
      {key:'reasoning',label:'Reasoning',type:'select',options:['','minimal','low','medium','high','xhigh']},
      {key:'systemPrompt',label:'Системная инструкция',type:'textarea'},{key:'userPromptTemplate',label:'Шаблон запроса с {{input}}',type:'textarea'},
      {key:'toolBindingsJson',label:'MCP-вызовы перед AI: массив {connectionId, toolName, arguments}',type:'textarea'},{key:'outputSchema',label:'JSON Schema результата',type:'textarea',required:true},{key:'enabled',label:'Разрешить запуск агента',type:'checkbox'}]},
    {key:'providers',title:'AI-провайдеры',description:'Codex CLI должен быть установлен и авторизован отдельно. Другие провайдеры пока не реализованы.',columns:['name','kind','model','enabled'],fields:[
      {key:'name',label:'Название',required:true},{key:'kind',label:'Тип',type:'select',options:['codex-cli']},{key:'model',label:'ID модели (пусто — выбор Codex CLI)'},
      {key:'timeoutSeconds',label:'Тайм-аут в секундах, 5–900',type:'number',required:true},{key:'enabled',label:'Разрешить отправку данных AI-провайдеру',type:'checkbox'}]},
    {key:'mcp',title:'MCP-подключения',description:'Доверенные stdio/HTTP серверы. Автоматические вызовы — только read-only инструменты из явного списка разрешений.',columns:['name','transport','status','enabled','hasSecrets'],fields:[
 {key:'name',label:'Название',required:true},{key:'transport',label:'Транспорт',type:'select',options:['stdio','http']},
 {key:'endpointOrCommand',label:'Исполняемый файл или HTTPS endpoint',required:true},{key:'argumentsJson',label:'Аргументы запуска, JSON-массив',type:'textarea',required:true},
 {key:'permissionsJson',label:'Разрешённые read-only инструменты, JSON-массив имён',type:'textarea',required:true},
 {key:'enabled',label:'Разрешить подключение к этому серверу',type:'checkbox'}]},
    {key:'browser-profiles',title:'Браузерные профили',description:'Отдельные профили Playwright для ручной авторизации. Cookies хранятся локально.',columns:['name','busy'],fields:[{key:'name',label:'Название',required:true}]},
    {key:'tasks',title:'Планировщик',description:'Задания выполняет Worker независимо от браузера. Интервалы считаются в UTC.',columns:['name','kind','intervalMinutes','enabled','nextRunAt'],fields:[
      {key:'name',label:'Название',required:true},{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},
      {key:'kind',label:'Действие',type:'select',options:['HealthCheck','BrowserDryRun','Integration','Workflow']},{key:'intervalMinutes',label:'Интервал в минутах',type:'number',required:true},
      {key:'integrationId',label:'Интеграция',type:'select',source:'integrations'},{key:'timeoutSeconds',label:'Тайм-аут, секунды (5–900)',type:'number'},{key:'maxRetries',label:'Повторы после ошибки (0–5)',type:'number'},
      {key:'timeZone',label:'Часовой пояс IANA',required:true},{key:'enabled',label:'Включить расписание',type:'checkbox'}]},
    {key:'integrations',title:'Интеграции',description:'CSV и Google Sheets: импорт после подтверждения. Gmail и JSON: чтение событий без отправки ответов.',columns:['name','kind','lastStatus','importConfirmed','enabled'],fields:[
 {key:'name',label:'Название',required:true},{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},
 {key:'kind',label:'Источник',type:'select',options:['csv','google-sheets','gmail','feedback-json','hh-status','status-json']},
 {key:'jobPlatformId',label:'Площадка журнала или проверки статусов',type:'select',source:'platforms'},
 {key:'mailAgentId',label:'Модель разбора Gmail (через агента)',type:'select',source:'mail-agents'},
 {key:'endpoint',label:'API endpoint (для сетевого источника)'},{key:'mappingJson',label:'JSON-сопоставление: externalId, title, employer, url, description, applicationState → заголовки столбцов',type:'textarea'},
 {key:'conflictPolicy',label:'Конфликты вакансий',type:'select',options:['keep-local','update-vacancies']},
 {key:'enabled',label:'Разрешить чтение внешнего источника',type:'checkbox'}]},
 {key:'imports',title:'Импорт журналов',description:'Проверьте строки перед подтверждением. Исторические отклики блокируют повторные заявки.',columns:['integrationId','status','rowCount','added','duplicates','conflicts','createdAt']},
 {key:'imported-applications',title:'История из журналов',description:'Подтверждённый импорт исторических заявок. Эти вакансии исключаются из новых откликов.',columns:['candidateProfileId','vacancyId','sourceState','sourceKey']},
 {key:'interactions',title:'Взаимодействия',description:'Сообщения, связанные с откликами автоматически или вручную.',columns:['jobApplicationId','category','message','createdAt']},
 {key:'feedback',title:'Обратная связь',description:'Сообщения из настроенных источников. Ответы автоматически не отправляются.',columns:['subject','category','analyzed','receivedAt','needsAction']},
    {key:'application-attempts',title:'Попытки отправки',description:'Только изолированный тестовый адаптер. Pending и SubmissionUnknown запрещают повторную отправку.',columns:['jobApplicationId','outcome','evidence','createdAt']},
    {key:'runs',title:'Запуски заданий',description:'История работы фонового процесса.',columns:['scheduledTaskId','status','message','createdAt','finishedAt']},
    {key:'executions',title:'Запуски агентов',description:'Модель и версия конфигурации. Вход и выход по умолчанию не сохраняются.',columns:['agentProfileId','configurationVersion','model','status','error','createdAt']},
    {key:'logs',title:'Журнал событий',description:'История изменений рабочего пространства.',columns:['entityType','action','entityId','createdAt']}
  ];
  current=computed(()=>this.sections.find(x=>x.key===this.section())!);
  rows=computed(()=>(this.data()[this.section()]??[]).filter(x=>(!this.profileId||!this.ownerProfile(x)||this.ownerProfile(x)===this.profileId)&&(!this.query()||JSON.stringify(x).toLowerCase().includes(this.query().toLowerCase()))));
  ownerProfile(row:Row):string|undefined{
    if(row['candidateProfileId'])return row['candidateProfileId'];
    const related:Record<string,string>={integrationId:'integrations',jobApplicationId:'applications',scheduledTaskId:'tasks',agentProfileId:'agents'};
    for(const [key,source] of Object.entries(related))if(row[key]){
      const parent=(this.data()[source]??[]).find(x=>x['id']===row[key]);
      return parent?.['candidateProfileId']??'unknown-owner';
    }
    return undefined;
  }
  formFields(){return (this.current().fields??[]).filter(field=>field.key!=='mailAgentId'||this.draft['kind']==='gmail');}
  fieldOptions(field:Field){return (field.options??[]).filter(x=>x!=='Workflow'||!!this.draft['id']);}
  labels:Record<string,string>={responseStatus:'Ответ работодателя',responseStatusAt:'Обновлено источником',category:'Категория',analyzed:'Разобрано',jobPlatformIds:'Площадки',isPrimary:'Основное',jobPlatformId:'Площадка',browserProfileId:'Браузерный профиль',externalResumeId:'ID резюме',workflowDefinitionId:'Workflow',stage:'Этап',prepared:'Подготовлено',rejected:'Отклонено',transport:'Транспорт',hasSecrets:'Секреты',lastStatus:'Последняя проверка',importConfirmed:'Импорт подтверждён',integrationId:'Интеграция',rowCount:'Строк',added:'Добавлено',duplicates:'Дубликаты',conflicts:'Конфликты',sourceState:'Статус источника',sourceKey:'ID источника',subject:'Тема',receivedAt:'Получено',needsAction:'Требует внимания',jobApplicationId:'Отклик',outcome:'Результат',evidence:'Подтверждение',name:'Название',email:'Email',skills:'Навыки',geography:'География',version:'Версия',originalName:'Файл',createdAt:'Создано',
    url:'URL',adapter:'Адаптер',title:'Должность',employer:'Работодатель',externalId:'Внешний ID',vacancyId:'Вакансия',candidateProfileId:'Профиль',state:'Состояние',
    revision:'Ревизия',taskType:'Этап',enabled:'Включён',kind:'Тип',model:'Модель',busy:'Открыт',intervalMinutes:'Интервал, мин',nextRunAt:'Следующий запуск',
    status:'Статус',detail:'Возможности',scheduledTaskId:'Задание',message:'Результат',finishedAt:'Завершено',agentProfileId:'Агент',
    configurationVersion:'Версия',error:'Ошибка',entityType:'Сущность',action:'Действие',entityId:'ID'};
  async ngOnInit(){const route=location.hash.slice(1);if(this.sections.some(x=>x.key===route))this.section.set(route);await this.refresh();}
  @HostListener('window:hashchange') onHashChange(){const key=location.hash.slice(1);if(key!==this.section()&&this.sections.some(x=>x.key===key))this.navigate(key);}
  navigate(key:string){this.section.set(key);location.hash=key;this.query.set('');this.editing.set(false);this.result.set('');this.activeAgent=null;this.draftApplication=null;this.browserDraft=null;this.browserPreview=null;this.feedbackEvent=null;this.importIntegration=null;this.sheetIntegration=null;this.sheetPreview=null;this.secretConnection=null;this.secretJson='{}';this.error.set('');this.message.set('');}
  async api(path:string,method='GET',body?:unknown){
    const response=await fetch('/api/'+path,{method,headers:{'X-JobFlow-Client':'local-ui',...(body instanceof FormData?{}:{'Content-Type':'application/json'})},
      body:body===undefined?undefined:body instanceof FormData?body:JSON.stringify(body)});
    const text=await response.text();let result:any;
    try{result=text?JSON.parse(text):null;}catch{throw new Error('Неожиданный ответ API. Проверьте сервер.');}
    if(!response.ok)throw new Error(result?.error??result?.detail??'Ошибка сервера: '+response.status);
    return result;
  }
  async refresh(){this.loading.set(true);try{
    const entries=await Promise.all(this.sections.filter(x=>x.columns).map(async x=>[x.key,await this.api(x.key)] as const));
    this.data.set(Object.fromEntries(entries));this.status.set(await this.api('status'));
    if(!this.profileId&&this.data()['profiles']?.length)this.profileId=this.data()['profiles'][0]['id'];
  }catch(e){this.error.set((e as Error).message);}finally{this.loading.set(false);}}
  create(){this.draft={mappingJson:'{"externalId":"Id","title":"Title","employer":"Employer","url":"Url","description":"Description","applicationState":"Status"}',conflictPolicy:'keep-local',transport:'stdio',argumentsJson:'[]',permissionsJson:'[]',toolBindingsJson:'[]',rulesJson:'{}',candidateProfileId:this.profileId,adapter:'search-only',kind:this.section()==='tasks'?'HealthCheck':this.section()==='integrations'?'csv':'codex-cli',timeoutSeconds:120,
    intervalMinutes:60,maxRetries:2,timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,enabled:false,taskType:'Evaluation',userPromptTemplate:'{{input}}',
    systemPrompt:'Use only verified candidate facts. Clearly identify uncertainty.',
    outputSchema:'{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}'};this.editing.set(true);this.error.set('');}
  edit(row:Row){this.draft={...row};this.editing.set(true);}
  copy(row:Row){this.draft={...row,name:row['name']+' — копия',enabled:false};delete this.draft['id'];this.editing.set(true);}
  options(field:Field){if(field.source==='mail-agents')return (this.data()['agents']??[]).filter(x=>x['taskType']==='MailAnalysis'&&x['candidateProfileId']===this.draft['candidateProfileId']);let list=field.source?this.data()[field.source]??[]:[];if(field.source==='resumes')list=list.filter(x=>x['candidateProfileId']===this.draft['candidateProfileId']);return list;}
  optionId(field:Field,row:Row){return field.source==='resumes'?row['versionId']:row['id'];}
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
  async save(){await this.action(async()=>{if(this.section()==='integrations'&&this.draft['kind']!=='gmail')this.draft['mailAgentId']=null;for(const key of ['evaluationAgentId','letterAgentId','mailAgentId','jobPlatformId','integrationId','browserProfileId'])if(this.draft[key]==='')this.draft[key]=null;await this.api(this.section()+(this.draft['id']?'/'+this.draft['id']:''),this.draft['id']?'PUT':'POST',this.draft);this.editing.set(false);this.message.set('Сохранено.');});}
  chooseFile(event:Event){this.file=(event.target as HTMLInputElement).files?.[0]??null;}
  uniqueResumes(){return [...new Map((this.data()['resumes']??[]).filter(x=>x['candidateProfileId']===this.profileId).map(x=>[x['id'],x])).values()];}
  async upload(){if(!this.file||!this.profileId){this.error.set('Выберите профиль и PDF.');return;}await this.action(async()=>{
    const form=new FormData();form.append('profileId',this.profileId);form.append('file',this.file!);if(this.resumeId)form.append('resumeId',this.resumeId);
    await this.api('resumes','POST',form);this.message.set('Версия резюме загружена.');this.file=null;});}
  async action(task:()=>Promise<void>){this.saving.set(true);this.error.set('');this.message.set('');try{await task();await this.refresh();}
    catch(e){this.error.set((e as Error).message);}finally{this.saving.set(false);}}
  async command(path:string,body:unknown={}){await this.action(async()=>{const value=await this.api(path,'POST',body);this.result.set(JSON.stringify(value,null,2));this.message.set((path.startsWith('tasks/')||path.startsWith('workflows/'))&&path.endsWith('/run')?'Задание поставлено в очередь. Проверьте «Запуски заданий»; Worker должен быть запущен.':'Действие выполнено.');});}
  async evaluate(row:Row){if(!this.profileId){this.error.set('Сначала выберите профиль.');return;}await this.command('vacancies/'+row['id']+'/evaluate',{profileId:this.profileId});}
  async execute(){if(this.activeAgent)await this.command('agents/'+this.activeAgent['id']+'/execute',{input:this.agentInput});}
  vacancyUrl(row:Row){
    const url=(this.data()['vacancies']??[]).find(v=>v['id']===row['vacancyId'])?.['url'];
    return typeof url==='string'&&['https://','http://'].some(prefix=>url.toLowerCase().startsWith(prefix))?url:null;
  }
  transitions(row:Row){const allowed:Record<string,string[]>={Discovered:['Evaluating'],Evaluating:['Rejected','Drafting','Blocked'],Drafting:['ReadyForReview','Blocked'],
    ReadyForReview:['Drafting','ReadyToSubmit','Rejected'],ReadyToSubmit:['Blocked'],Blocked:['Drafting','Closed'],Submitted:['Closed'],EmployerResponded:['Closed'],SubmissionUnknown:['Closed']};return allowed[row['state']]??[];}
  display(key:string,value:any):string{const map:Record<string,string>={candidateProfileId:'profiles',vacancyId:'vacancies',scheduledTaskId:'tasks',agentProfileId:'agents',integrationId:'integrations',workflowDefinitionId:'workflows',jobPlatformId:'platforms',browserProfileId:'browser-profiles',jobApplicationId:'applications'};
    if(map[key]){const row=(this.data()[map[key]]??[]).find(x=>x['id']===value);if(row)return row['name']??row['title']??(row['vacancyId']?this.display('vacancyId',row['vacancyId']):value);}
    if(typeof value==='boolean')return value?'Да':'Нет';if(key.endsWith('At')&&value)return new Date(value).toLocaleString();return String(value??'—');}
  async removeBrowser(row:Row){if(!confirm('Удалить профиль «'+row['name']+'» и сохранённую авторизацию?'))return;
    await this.action(async()=>{await this.api('browser-profiles/'+row['id']+'?confirm=true','DELETE');});}
}
