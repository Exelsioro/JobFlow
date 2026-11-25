import { Component, computed, input, output } from '@angular/core';
import type { Row } from './workspace-sections';
import { runStatusLabels, workspaceTime } from './workspace-format';

const queues = [
  {state: 'SubmissionUnknown', title: 'Проверить отправку', hint: 'Нет подтверждения площадки'},
  {state: 'Blocked', title: 'Разобрать остановку', hint: 'Подготовка остановлена'},
  {state: 'ReadyForReview', title: 'Проверить материалы', hint: 'Ожидает вашего решения'},
];

@Component({
  selector: 'app-workspace-dashboard',
  templateUrl: './workspace-dashboard.html',
  styleUrl: './workspace-dashboard.css',
})
export class WorkspaceDashboard {
  readonly records = input.required<Record<string, Row[]>>();
  readonly profile = input.required<string>();
  readonly busy = input(false);
  readonly filter = output<string>();
  readonly createProfile = output<void>();
  readonly testBrowser = output<void>();
  readonly hasProfiles = computed(() => this.records()['profiles']?.length);
  readonly applications = computed(() => this.records()['applications']?.filter(row =>
    row['candidateProfileId'] === this.profile()));
  readonly queue = computed(() => {
    const rows = this.applications();
    if (!rows) return null;
    const vacancies = new Map((this.records()['vacancies'] ?? []).map(row => [row['id'], row]));
    const groups = queues.map(group => ({
      ...group,
      rows: rows.filter(row => row['state'] === group.state)
        .sort((a, b) => String(b['createdAt']).localeCompare(String(a['createdAt']))),
    }));
    const pending = groups.flatMap(group => group.rows.map(row => ({
      id: row['id'], isTest: row['isTest'], vacancy: vacancies.get(row['vacancyId']), group,
    })));
    return {groups, total: pending.length, preview: pending.slice(0, 5)};
  });
  readonly runs = computed(() => {
    const workflows = new Map((this.records()['workflows'] ?? []).map(row => [row['id'], row['name']]));
    return this.records()['workflow-runs']?.filter(run => run['candidateProfileId'] === this.profile())
      .sort((a,b) => String(b['createdAt']).localeCompare(String(a['createdAt'])))
      .slice(0, 4).map(run => ({
        row: run, name: workflows.get(run['workflowDefinitionId']) ?? 'Workflow', started: workspaceTime(run['createdAt'], true),
      }));
  });
  readonly runLabels = runStatusLabels;
}
