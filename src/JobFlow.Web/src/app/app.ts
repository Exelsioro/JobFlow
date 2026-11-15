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
  private selectedProfile=signal(''); get profileId(){return this.selectedProfile();} set profileId(value:string){this.selectedProfile.set(value);this.resumeId='';this.activeAgent=null;this.editing.set(false);} draft:Row={}; file:File|null=null; resumeId=''; agentInput=''; activeAgent:Row|null=null;
  sections:Section[]=[
    {key:'dashboard',title:'Обзор',description:'Ваш поиск работы — в одном рабочем пространстве.'},
    {key:'profiles',title:'Профили кандидата',description:'Опыт, предпочтения и правила отдельного профиля.',columns:['name','email','skills','geography'],fields:[
      {key:'name',label:'Название профиля',required:true},{key:'email',label:'Email',type:'email'},
      {key:'experience',label:'Подтверждённый опыт',type:'textarea'},{key:'skills',label:'Навыки через запятую'},
      {key:'education',label:'Образование',type:'textarea'},{key:'languages',label:'Языки'},{key:'preferredRoles',label:'Предпочитаемые роли'},
      {key:'geography',label:'География'},{key:'salary',label:'Зарплата и валюта'},{key:'relocation',label:'Релокация'},
      {key:'excludedEmployers',label:'Исключённые работодатели через запятую'},{key:'rules',label:'Правила поиска',type:'textarea'}]},
    {key:'resumes',title:'Резюме',description:'Локальные PDF и версии. Каждый отклик привязан к конкретной версии.',columns:['name','version','originalName','createdAt']},
    {key:'platforms',title:'Площадки',description:'Добавление URL не включает автоматическую отправку.',columns:['name','url','adapter'],fields:[
      {key:'name',label:'Название',required:true},{key:'url',label:'URL',type:'url',required:true},
      {key:'adapter',label:'Адаптер',type:'select',options:['search-only','test-form']},{key:'geography',label:'География'},{key:'searchFilters',label:'Фильтры поиска',type:'textarea'}]},
    {key:'vacancies',title:'Вакансии',description:'Ручной импорт и предварительная оценка для выбранного профиля.',columns:['title','employer','externalId','createdAt'],fields:[
      {key:'jobPlatformId',label:'Площадка',type:'select',source:'platforms',required:true},{key:'externalId',label:'ID на площадке',required:true},
      {key:'title',label:'Должность',required:true},{key:'employer',label:'Работодатель',required:true},{key:'url',label:'Ссылка',type:'url'},
      {key:'description',label:'Описание и требования',type:'textarea',required:true}]},
    {key:'applications',title:'Отклики',description:'Подготовка и проверка. Реальная отправка отключена.',columns:['vacancyId','candidateProfileId','state','revision'],fields:[
      {key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},{key:'vacancyId',label:'Вакансия',type:'select',source:'vacancies',required:true},
      {key:'resumeVersionId',label:'Версия резюме',type:'select',source:'resumes',required:true},{key:'coverLetter',label:'Сопроводительное письмо',type:'textarea'}]},
    {key:'agents',title:'Агенты',description:'Версии конфигурации сохраняются. Включение разрешает вызов провайдера.',columns:['name','taskType','version','enabled'],fields:[
      {key:'name',label:'Название',required:true},{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},
      {key:'modelProviderId',label:'Провайдер',type:'select',source:'providers',required:true},
      {key:'taskType',label:'Этап',type:'select',options:['Discovery','Evaluation','CoverLetter','Preparation','Submission','Monitoring','Custom']},
      {key:'description',label:'Описание'},{key:'model',label:'Модель (пусто — модель провайдера)'},
      {key:'reasoning',label:'Reasoning',type:'select',options:['','minimal','low','medium','high','xhigh']},
      {key:'systemPrompt',label:'Системная инструкция',type:'textarea'},{key:'userPromptTemplate',label:'Шаблон запроса с {{input}}',type:'textarea'},
      {key:'outputSchema',label:'JSON Schema результата',type:'textarea',required:true},{key:'enabled',label:'Разрешить запуск агента',type:'checkbox'}]},
    {key:'providers',title:'AI-провайдеры',description:'Codex CLI должен быть установлен и авторизован отдельно. Другие провайдеры пока не реализованы.',columns:['name','kind','model','enabled'],fields:[
      {key:'name',label:'Название',required:true},{key:'kind',label:'Тип',type:'select',options:['codex-cli']},{key:'model',label:'ID модели (пусто — выбор Codex CLI)'},
      {key:'timeoutSeconds',label:'Тайм-аут в секундах, 5–900',type:'number',required:true},{key:'enabled',label:'Разрешить отправку данных AI-провайдеру',type:'checkbox'}]},
    {key:'mcp',title:'MCP-подключения',description:'MCP-клиент и разграничение разрешений ещё не реализованы.'},
    {key:'browser-profiles',title:'Браузерные профили',description:'Отдельные профили Playwright для ручной авторизации. Cookies хранятся локально.',columns:['name','busy'],fields:[{key:'name',label:'Название',required:true}]},
    {key:'tasks',title:'Планировщик',description:'Задания выполняет Worker независимо от браузера. Интервалы считаются в UTC.',columns:['name','kind','intervalMinutes','enabled','nextRunAt'],fields:[
      {key:'name',label:'Название',required:true},{key:'candidateProfileId',label:'Профиль',type:'select',source:'profiles',required:true},
      {key:'kind',label:'Действие',type:'select',options:['HealthCheck','BrowserDryRun']},{key:'intervalMinutes',label:'Интервал в минутах',type:'number',required:true},
      {key:'timeZone',label:'Часовой пояс IANA',required:true},{key:'enabled',label:'Включить расписание',type:'checkbox'}]},
    {key:'integrations',title:'Интеграции',description:'База JobFlow — основной источник данных.',columns:['name','status','detail']},
    {key:'runs',title:'Запуски заданий',description:'История работы фонового процесса.',columns:['scheduledTaskId','status','message','createdAt','finishedAt']},
    {key:'executions',title:'Запуски агентов',description:'Модель и версия конфигурации. Вход и выход по умолчанию не сохраняются.',columns:['agentProfileId','configurationVersion','model','status','error','createdAt']},
    {key:'logs',title:'Журнал событий',description:'История изменений рабочего пространства.',columns:['entityType','action','entityId','createdAt']}
  ];
  current=computed(()=>this.sections.find(x=>x.key===this.section())!);
  rows=computed(()=>(this.data()[this.section()]??[]).filter(x=>(!this.profileId||!x['candidateProfileId']||x['candidateProfileId']===this.profileId)&&(!this.query()||JSON.stringify(x).toLowerCase().includes(this.query().toLowerCase()))));
  labels:Record<string,string>={name:'Название',email:'Email',skills:'Навыки',geography:'География',version:'Версия',originalName:'Файл',createdAt:'Создано',
    url:'URL',adapter:'Адаптер',title:'Должность',employer:'Работодатель',externalId:'Внешний ID',vacancyId:'Вакансия',candidateProfileId:'Профиль',state:'Состояние',
    revision:'Ревизия',taskType:'Этап',enabled:'Включён',kind:'Тип',model:'Модель',busy:'Открыт',intervalMinutes:'Интервал, мин',nextRunAt:'Следующий запуск',
    status:'Статус',detail:'Возможности',scheduledTaskId:'Задание',message:'Результат',finishedAt:'Завершено',agentProfileId:'Агент',
    configurationVersion:'Версия',error:'Ошибка',entityType:'Сущность',action:'Действие',entityId:'ID'};
  async ngOnInit(){const route=location.hash.slice(1);if(this.sections.some(x=>x.key===route))this.section.set(route);await this.refresh();}
  @HostListener('window:hashchange') onHashChange(){const key=location.hash.slice(1);if(key!==this.section()&&this.sections.some(x=>x.key===key))this.navigate(key);}
  navigate(key:string){this.section.set(key);location.hash=key;this.query.set('');this.editing.set(false);this.result.set('');this.activeAgent=null;this.error.set('');this.message.set('');}
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
  create(){this.draft={candidateProfileId:this.profileId,adapter:'search-only',kind:this.section()==='tasks'?'HealthCheck':'codex-cli',timeoutSeconds:120,
    intervalMinutes:60,timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,enabled:false,taskType:'Evaluation',userPromptTemplate:'{{input}}',
    systemPrompt:'Use only verified candidate facts. Clearly identify uncertainty.',
    outputSchema:'{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}'};this.editing.set(true);this.error.set('');}
  edit(row:Row){this.draft={...row};this.editing.set(true);}
  copy(row:Row){this.draft={...row,name:row['name']+' — копия',enabled:false};delete this.draft['id'];this.editing.set(true);}
  options(field:Field){let list=field.source?this.data()[field.source]??[]:[];if(field.source==='resumes')list=list.filter(x=>x['candidateProfileId']===this.draft['candidateProfileId']);return list;}
  optionId(field:Field,row:Row){return field.source==='resumes'?row['versionId']:row['id'];}
  optionName(row:Row){return(row['name']??row['title']??row['id'])+(row['version']?' · v'+row['version']:'');}
  async save(){await this.action(async()=>{await this.api(this.section()+(this.draft['id']?'/'+this.draft['id']:''),this.draft['id']?'PUT':'POST',this.draft);this.editing.set(false);this.message.set('Сохранено.');});}
  chooseFile(event:Event){this.file=(event.target as HTMLInputElement).files?.[0]??null;}
  uniqueResumes(){return [...new Map((this.data()['resumes']??[]).filter(x=>x['candidateProfileId']===this.profileId).map(x=>[x['id'],x])).values()];}
  async upload(){if(!this.file||!this.profileId){this.error.set('Выберите профиль и PDF.');return;}await this.action(async()=>{
    const form=new FormData();form.append('profileId',this.profileId);form.append('file',this.file!);if(this.resumeId)form.append('resumeId',this.resumeId);
    await this.api('resumes','POST',form);this.message.set('Версия резюме загружена.');this.file=null;});}
  async action(task:()=>Promise<void>){this.saving.set(true);this.error.set('');this.message.set('');try{await task();await this.refresh();}
    catch(e){this.error.set((e as Error).message);}finally{this.saving.set(false);}}
  async command(path:string,body:unknown={}){await this.action(async()=>{const value=await this.api(path,'POST',body);this.result.set(JSON.stringify(value,null,2));this.message.set(path.startsWith('tasks/')&&path.endsWith('/run')?'Задание поставлено в очередь. Проверьте «Запуски заданий»; Worker должен быть запущен.':'Действие выполнено.');});}
  async evaluate(row:Row){if(!this.profileId){this.error.set('Сначала выберите профиль.');return;}await this.command('vacancies/'+row['id']+'/evaluate',{profileId:this.profileId});}
  async execute(){if(this.activeAgent)await this.command('agents/'+this.activeAgent['id']+'/execute',{input:this.agentInput});}
  transitions(row:Row){const allowed:Record<string,string[]>={Discovered:['Evaluating'],Evaluating:['Rejected','Drafting','Blocked'],Drafting:['ReadyForReview','Blocked'],
    ReadyForReview:['Drafting','ReadyToSubmit','Rejected'],ReadyToSubmit:['Blocked'],Blocked:['Drafting','Closed'],Submitted:['Closed'],EmployerResponded:['Closed'],SubmissionUnknown:['Closed']};return allowed[row['state']]??[];}
  display(key:string,value:any):string{const map:Record<string,string>={candidateProfileId:'profiles',vacancyId:'vacancies',scheduledTaskId:'tasks',agentProfileId:'agents'};
    if(map[key]){const row=(this.data()[map[key]]??[]).find(x=>x['id']===value);if(row)return row['name']??row['title'];}
    if(typeof value==='boolean')return value?'Да':'Нет';if(key.endsWith('At')&&value)return new Date(value).toLocaleString();return String(value??'—');}
  async removeBrowser(row:Row){if(!confirm('Удалить профиль «'+row['name']+'» и сохранённую авторизацию?'))return;
    await this.action(async()=>{await this.api('browser-profiles/'+row['id']+'?confirm=true','DELETE');});}
}
