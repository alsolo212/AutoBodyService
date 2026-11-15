import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Subscription, finalize } from 'rxjs';
import { AuthService } from '../../../core/auth';

interface AuditEntry { id: string; occurredAtUtc: string; actorId: string; action: string; objectType: string; objectId: string; details: Record<string, unknown>; }
interface AuditPage { total: number; page: number; pageSize: number; items: AuditEntry[]; }
const actions: Record<string, string> = {
  AppointmentStatusChanged: 'Зміна статусу замовлення', RepairPlanChanged: 'Зміна плану ремонту',
  InspectionScheduleChanged: 'Зміна тижневого розкладу', InspectionExceptionChanged: 'Зміна винятку розкладу',
  AssignmentCreated: 'Призначення працівника', AssignmentReleased: 'Зняття призначення',
  AccountPasswordReset: 'Скидання пароля', AccountAdminChanged: 'Зміна ролі адміністратора', AccountBlockedChanged: 'Зміна блокування',
  ExpenseCreated: 'Додавання витрати', ExpenseCorrected: 'Виправлення витрати',
  PriceItemCreated: 'Додавання послуги', PriceItemChanged: 'Зміна послуги', ManualContactRecorded: 'Ручний контакт',
};
const objects: Record<string, string> = { Appointment: 'Замовлення', EmployeeAssignment: 'Призначення', ApplicationUser: 'Обліковий запис', MonthlyExpense: 'Витрата', InspectionSchedule: 'Тижневий розклад', InspectionDate: 'Дата огляду', PriceListItem: 'Послуга' };
const statuses: Record<string, string> = { AwaitingInspection: 'Очікує огляду', Scheduled: 'Заплановано', InProgress: 'У роботі', AwaitingPayment: 'Очікує оплати', Completed: 'Завершено', Cancelled: 'Скасовано' };
const modes: Record<string, string> = { Weekly: 'За тижневим розкладом', Closed: 'Закрито', Replace: 'Окремий розклад', Open: 'Відкрито' };
const contacts: Record<string, string> = { NewAppointment: 'Новий запис', ScheduleConflictOrReschedule: 'Конфлікт розкладу або перенесення', Agreement: 'Узгодження', ReadyForPayment: 'Готовність до оплати і видачі' };
const yesNo = (value: unknown) => value === true ? 'так' : value === false ? 'ні' : '—';
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
function dayStart(date: string, next = false): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return null;
  const instant = new Date(`${date}T00:00:00.000Z`);
  if (!Number.isFinite(instant.getTime()) || instant.toISOString().slice(0, 10) !== date) return null;
  if (next) instant.setUTCDate(instant.getUTCDate() + 1);
  return instant.toISOString();
}
function schedule(value: unknown): string {
  if (value === null) return 'немає';
  if (Array.isArray(value)) {
    const days = ['нд', 'пн', 'вт', 'ср', 'чт', 'пт', 'сб'];
    return value.length ? value.map(item => { const i = record(item); return `${days[Number(i['DayOfWeek'])] ?? '—'} ${i['Start'] ?? '—'}–${i['End'] ?? '—'}`; }).join('; ') : 'немає інтервалів';
  }
  const d = record(value);
  const intervals = Array.isArray(d['intervals']) ? d['intervals'].map(item => { const i = record(item); return `${i['Start'] ?? '—'}–${i['End'] ?? '—'}`; }).join(', ') : '';
  const slots = Array.isArray(d['slots']) ? d['slots'].map(item => { const i = record(item); return `${i['Start'] ?? '—'}: ${modes[String(i['mode'])] ?? '—'}`; }).join(', ') : '';
  return [modes[String(d['mode'])] ?? '—', intervals, slots].filter(Boolean).join('; ');
}
function expense(value: unknown): string {
  if (value === null) return 'немає';
  const d = record(value);
  const amount = typeof d['amountExact'] === 'string' ? d['amountExact'].replace('.', ',') + ' ₴' : 'див. фінансовий звіт';
  return `${amount}; опис: ${yesNo(d['hasDescription'])}`;
}
export function auditDetails(entry: AuditEntry): string[] {
  const d = entry.details;
  switch (entry.action) {
    case 'AppointmentStatusChanged': return [`${statuses[String(d['from'])] ?? '—'} → ${statuses[String(d['to'])] ?? '—'}`,
      ...(d['to'] === 'Cancelled' ? ['Причина зберігається в історії замовлення.'] : []),
      ...(d['statusEventId'] ? [`Подія статусу: ${d['statusEventId']}`] : [])];
    case 'RepairPlanChanged': return [`Вартість змінено: ${yesNo(d['priceChanged'])}`, `Терміни змінено: ${yesNo(d['datesChanged'])}`];
    case 'AccountAdminChanged': return [`Роль адміністратора: ${d['value'] === true ? 'надано' : d['value'] === false ? 'відкликано' : '—'}`];
    case 'AccountBlockedChanged': return [`Обліковий запис: ${d['value'] === true ? 'заблоковано' : d['value'] === false ? 'розблоковано' : '—'}`];
    case 'AssignmentCreated': case 'AssignmentReleased': return [`Замовлення: ${d['appointmentId'] ?? '—'}`, `Працівник: ${d['employeeId'] ?? '—'}`, ...(entry.action === 'AssignmentReleased' ? [`Автоматично: ${yesNo(d['automatic'])}`] : [])];
    case 'ExpenseCreated': case 'ExpenseCorrected': return [`Було: ${expense(d['before'])}`, `Стало: ${expense(d['after'])}`, `Опис змінено: ${yesNo(d['descriptionChanged'])}`];
    case 'InspectionScheduleChanged': case 'InspectionExceptionChanged': return [`Було: ${schedule(d['before'])}`, `Стало: ${schedule(d['after'])}`];
    case 'ManualContactRecorded': return [`Привід: ${contacts[String(d['reason'])] ?? '—'}`];
    case 'AccountPasswordReset': return ['Пароль не зберігається в журналі.'];
    default: return ['Додаткових відомостей немає.'];
  }
}

@Component({
  selector: 'app-audit-log', imports: [ReactiveFormsModule], templateUrl: './audit-log.html', styleUrl: './audit-log.scss',
})
export class AuditLog {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly destroy = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private request?: Subscription;
  private key = this.sessionKey();
  private page = 1;
  private applied = { fromDate: '', toDate: '', action: '', actorId: '', direction: 'desc' };
  readonly form = this.fb.group({ fromDate: [''], toDate: [''], action: [''], actorId: ['', Validators.maxLength(450)], direction: ['desc'] });
  readonly choices = Object.entries(actions).map(([value, label]) => ({ value, label }));
  readonly result = signal<AuditPage | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly validation = signal('');
  readonly details = auditDetails;
  readonly actionLabel = (action: string) => actions[action] ?? action;
  readonly objectLabel = (object: string) => objects[object] ?? object;
  readonly time = (value: string) => new Intl.DateTimeFormat('uk-UA', { timeZone: 'UTC', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).format(new Date(value)) + ' UTC';

  constructor() {
    effect(() => {
      const key = this.sessionKey();
      if (key === this.key) return;
      this.key = key; this.request?.unsubscribe(); this.result.set(null); this.error.set('');
      if (this.allowed()) this.load();
    });
    this.destroy.onDestroy(() => this.request?.unsubscribe());
    if (this.allowed()) this.load();
  }
  private allowed() { const s = this.auth.session(); return !!s && !s.mustChangePassword && s.roles.includes('SuperAdmin'); }
  private sessionKey() { const s = this.auth.session(); return s ? `${s.phone}|${[...s.roles].sort().join(',')}|${s.mustChangePassword}` : ''; }
  apply() {
    this.form.markAllAsTouched(); const raw = this.form.getRawValue();
    if (this.form.invalid || (raw.fromDate && !dayStart(raw.fromDate)) || (raw.toDate && !dayStart(raw.toDate, true)) || (raw.fromDate && raw.toDate && raw.fromDate > raw.toDate)) {
      this.validation.set('Перевірте дати та ідентифікатор виконавця.'); return;
    }
    this.validation.set(''); this.applied = { ...raw, actorId: raw.actorId.trim() }; this.page = 1; this.load();
  }
  changePage(delta: number) { if (!this.loading()) { this.page += delta; this.load(); } }
  load() {
    if (!this.allowed()) return;
    this.request?.unsubscribe(); this.loading.set(true); this.error.set(''); this.result.set(null);
    let params = new HttpParams().set('direction', this.applied.direction).set('page', this.page).set('pageSize', 10);
    if (this.applied.fromDate) params = params.set('fromUtc', dayStart(this.applied.fromDate)!);
    if (this.applied.toDate) params = params.set('toUtc', dayStart(this.applied.toDate, true)!);
    if (this.applied.action) params = params.set('action', this.applied.action);
    if (this.applied.actorId) params = params.set('actorId', this.applied.actorId);
    const key = this.key, version = this.auth.sessionVersion();
    this.request = this.http.get<AuditPage>('/api/admin/audit-log', { params }).pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroy)).subscribe({
      next: page => { if (key === this.sessionKey() && version === this.auth.sessionVersion()) this.result.set(page); },
      error: (err: unknown) => {
        if (key !== this.sessionKey() || version !== this.auth.sessionVersion()) return;
        if (err instanceof HttpErrorResponse && err.status === 401) { this.auth.invalidateSession(); return; }
        if (err instanceof HttpErrorResponse && err.status === 403) {
          this.error.set('Доступ до журналу заборонено.');
          this.auth.getSession().pipe(takeUntilDestroyed(this.destroy)).subscribe({ error: () => {} }); return;
        }
        this.error.set(err instanceof HttpErrorResponse && err.status === 400 ? 'Перевірте параметри журналу.' : 'Не вдалося завантажити журнал.');
      },
    });
  }
}
