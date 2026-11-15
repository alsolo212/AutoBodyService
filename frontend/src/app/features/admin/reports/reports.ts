import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, effect, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Subscription, finalize } from 'rxjs';
import { AuthService, AuthSession } from '../../../core/auth';
import { kyivTime } from '../../../shared/time/kyiv-time';
import { decimalLiteral } from '../prices/prices-editor';
import { Expense, ExpensePage, FinanceReport, ReportsApi } from './reports-api';
import { AuditLog } from './audit-log';

function actorKey(session: AuthSession | null): string | null {
  return session ? `${session.phone}|${[...session.roles].sort().join(',')}|${session.mustChangePassword}` : null;
}
function currentKyivPeriod(): { year: number; month: number } {
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', year: 'numeric', month: 'numeric' }).formatToParts(new Date());
  return { year: Number(parts.find(x => x.type === 'year')!.value), month: Number(parts.find(x => x.type === 'month')!.value) };
}
export function formatUah(value: string): string {
  const negative = value.startsWith('-');
  const [whole, fraction = ''] = (negative ? value.slice(1) : value).split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');
  return `${negative ? '−' : ''}${grouped},${fraction.padEnd(2, '0')} ₴`;
}
const optionalDecimal: ValidatorFn = control => !String(control.value).trim() || decimalLiteral(String(control.value)) !== null ? null : { decimal: true };
const positiveDecimal: ValidatorFn = control => {
  const value = decimalLiteral(String(control.value));
  return value !== null && value !== '0' ? null : { positive: true };
};
const descriptionValidator: ValidatorFn = control => /\p{Cc}/u.test(String(control.value ?? '')) ? { invalid: true } : null;
const integerValidator: ValidatorFn = control => Number.isInteger(control.value) ? null : { integer: true };

@Injectable({ providedIn: 'root' })
export class ReportsContext {
  key: string | null = null;
  period = currentKyivPeriod();
  tab: 'finance' | 'audit' = 'finance';
  constructor() {
    try {
      const saved = JSON.parse(sessionStorage.getItem('reports-view') ?? 'null');
      if (saved && Number.isInteger(saved.year) && saved.year >= 2 && saved.year <= 9998 &&
          Number.isInteger(saved.month) && saved.month >= 1 && saved.month <= 12 && (saved.tab === 'finance' || saved.tab === 'audit')) {
        this.period = { year: saved.year, month: saved.month }; this.tab = saved.tab;
      }
    } catch { /* Browser storage is optional. */ }
  }
  save() {
    try { sessionStorage.setItem('reports-view', JSON.stringify({ ...this.period, tab: this.tab })); } catch { /* Keep the in-memory view. */ }
  }
}

@Component({ selector: 'app-reports', imports: [ReactiveFormsModule, RouterLink, AuditLog], templateUrl: './reports.html', styleUrl: './reports.scss' })
export class Reports {
  private readonly api = inject(ReportsApi);
  private readonly context = inject(ReportsContext);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private key = actorKey(this.auth.session());
  private readonly initial = (!this.context.key || this.context.key === this.key) && this.key ? this.context.period : currentKyivPeriod();
  readonly activeTab = signal<'finance' | 'audit'>((!this.context.key || this.context.key === this.key) && this.key ? this.context.tab : 'finance');
  readonly visitedAudit = signal(this.activeTab() === 'audit');
  private request?: Subscription;
  private expenseRequest?: Subscription;
  readonly period = signal(this.initial);
  readonly periodForm = this.fb.group({ year: [this.initial.year, [Validators.required, Validators.min(2), Validators.max(9998), integerValidator]], month: [this.initial.month, [Validators.required, Validators.min(1), Validators.max(12), integerValidator]] });
  readonly filterForm = this.fb.group({ fromDate: [''], toDate: [''], minAmount: ['', optionalDecimal], maxAmount: ['', optionalDecimal], sort: ['date'], direction: ['desc'] });
  readonly expenseSortForm = this.fb.group({ sort: ['date'], direction: ['desc'] });
  readonly expenseForm = this.fb.group({ amount: ['', positiveDecimal], description: ['', [Validators.maxLength(500), descriptionValidator]] });
  readonly months = ['Січень', 'Лютий', 'Березень', 'Квітень', 'Травень', 'Червень', 'Липень', 'Серпень', 'Вересень', 'Жовтень', 'Листопад', 'Грудень'];
  readonly report = signal<FinanceReport | null>(null);
  readonly expenses = signal<ExpensePage | null>(null);
  readonly loading = signal(false); readonly expensesLoading = signal(false); readonly saving = signal(false);
  readonly loadError = signal(''); readonly expensesError = signal(''); readonly actionError = signal(''); readonly notice = signal('');
  readonly editing = signal<Expense | null>(null); readonly conflict = signal(false);
  private orderPage = 1; private expensePage = 1;
  private filters = { fromDate: '', toDate: '', minAmount: '', maxAmount: '', sort: 'date', direction: 'desc' };
  private expenseSort = { sort: 'date', direction: 'desc' };
  readonly money = formatUah;
  readonly date = (value: string) => kyivTime(value, true);

  constructor() {
    this.context.key = this.key; this.context.period = this.initial; this.context.tab = this.activeTab();
    effect(() => {
      const session = this.auth.session(); const key = actorKey(session);
      if (key === this.key) return;
      this.key = key; this.context.key = key; this.context.period = currentKyivPeriod(); this.context.tab = 'finance'; this.context.save(); this.request?.unsubscribe(); this.expenseRequest?.unsubscribe(); this.report.set(null); this.expenses.set(null); this.editing.set(null);
      this.expenseForm.reset(); this.notice.set(''); this.actionError.set('');
      if (!session) { void this.router.navigateByUrl('/login'); return; }
      if (session.mustChangePassword) { void this.router.navigateByUrl('/account/change-temporary-password'); return; }
      if (!session.roles.includes('SuperAdmin')) { void this.router.navigateByUrl(session.roles.includes('Admin') ? '/admin' : '/account/appointments'); return; }
      this.load();
    });
    this.destroyRef.onDestroy(() => { this.request?.unsubscribe(); this.expenseRequest?.unsubscribe(); });
    if (this.auth.session()?.roles.includes('SuperAdmin')) this.load();
  }
  selectTab(tab: 'finance' | 'audit', moveFocus = false) {
    this.activeTab.set(tab); this.context.tab = tab; this.context.save();
    if (tab === 'audit') this.visitedAudit.set(true);
    if (moveFocus) document.getElementById(`${tab}-tab`)?.focus();
  }
  tabKey(event: KeyboardEvent) {
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      event.preventDefault(); this.selectTab(this.activeTab() === 'finance' ? 'audit' : 'finance', true);
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault(); this.selectTab(event.key === 'Home' ? 'finance' : 'audit', true);
    }
  }
  applyPeriod() {
    if (this.saving()) return;
    this.periodForm.markAllAsTouched();
    const { year, month } = this.periodForm.getRawValue();
    if (this.periodForm.invalid || !Number.isInteger(year) || !Number.isInteger(month)) return;
    this.period.set({ year, month }); this.context.period = { year, month }; this.context.save(); this.orderPage = this.expensePage = 1;
    this.filterForm.patchValue({ fromDate: '', toDate: '' }); this.filters = { ...this.filters, fromDate: '', toDate: '' };
    this.editing.set(null); this.conflict.set(false); this.expenseForm.reset(); this.actionError.set(''); this.notice.set('');
    this.load();
  }
  applyFilters() {
    if (this.saving()) return;
    this.filterForm.markAllAsTouched();
    if (this.filterForm.invalid) return;
    const raw = this.filterForm.getRawValue();
    this.filters = { ...raw, minAmount: raw.minAmount.trim() ? decimalLiteral(raw.minAmount)! : '', maxAmount: raw.maxAmount.trim() ? decimalLiteral(raw.maxAmount)! : '' };
    this.orderPage = 1; this.load();
  }
  applyExpenseSort() {
    if (this.saving()) return;
    this.expenseSort = this.expenseSortForm.getRawValue(); this.expensePage = 1; this.load();
  }
  changeOrderPage(delta: number) { if (!this.loading() && !this.saving()) { this.orderPage += delta; this.load(); } }
  changeExpensePage(delta: number) { if (!this.expensesLoading() && !this.saving()) { this.expensePage += delta; this.load(); } }
  load() {
    if (!this.auth.session()?.roles.includes('SuperAdmin')) return;
    this.request?.unsubscribe(); this.expenseRequest?.unsubscribe();
    this.loading.set(true); this.expensesLoading.set(true); this.loadError.set(''); this.expensesError.set('');
    this.report.set(null); this.expenses.set(null);
    const period = this.period(); const key = this.key; const sessionVersion = this.auth.sessionVersion();
    const current = () => key === actorKey(this.auth.session()) && sessionVersion === this.auth.sessionVersion();
    this.request = this.api.report({ ...period, page: this.orderPage, ...this.filters }).pipe(
      finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: report => { if (current()) this.report.set(report); },
      error: (error: unknown) => {
        if (!current()) return;
        this.loadError.set(this.message(error, 'Не вдалося завантажити звіт.')); this.checkAuthorization(error);
      },
    });
    this.expenseRequest = this.api.expenses(period.year, period.month, this.expenseSort.sort, this.expenseSort.direction, this.expensePage).pipe(
      finalize(() => this.expensesLoading.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: expenses => { if (current()) this.expenses.set(expenses); },
      error: (error: unknown) => {
        if (!current()) return;
        this.expensesError.set(this.message(error, 'Не вдалося завантажити витрати.')); this.checkAuthorization(error);
      },
    });
  }
  edit(expense: Expense) {
    if (this.saving()) return;
    this.editing.set(expense); this.conflict.set(false); this.actionError.set(''); this.notice.set('');
    this.expenseForm.reset({ amount: expense.amountExact.replace('.', ','), description: expense.description ?? '' });
    document.getElementById('expense-amount')?.focus();
  }
  cancelEdit() { this.editing.set(null); this.conflict.set(false); this.expenseForm.reset(); this.actionError.set(''); }
  save() {
    if (this.saving() || this.expensesLoading() || this.expensesError() || this.conflict() || !this.auth.session()?.roles.includes('SuperAdmin')) return;
    this.expenseForm.markAllAsTouched();
    if (this.expenseForm.invalid) return;
    const values = this.expenseForm.getRawValue(); const amount = decimalLiteral(values.amount);
    if (amount === null || amount === '0') return;
    const current = this.editing(); const key = this.key; const sessionVersion = this.auth.sessionVersion();
    this.saving.set(true); this.actionError.set(''); this.notice.set('');
    this.api.save(amount, values.description.trim() || null, current).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: item => {
        if (key !== actorKey(this.auth.session()) || sessionVersion !== this.auth.sessionVersion()) return;
        this.cancelEdit();
        this.notice.set(current ? `Витрату виправлено. Вона залишається за ${this.months[item.recordedMonth - 1].toLowerCase()} ${item.recordedYear} року.`
          : `Витрату додано за ${this.months[item.recordedMonth - 1].toLowerCase()} ${item.recordedYear} року за часом сервера.`);
        this.load();
      },
      error: (error: unknown) => {
        if (key !== actorKey(this.auth.session()) || sessionVersion !== this.auth.sessionVersion()) return;
        this.actionError.set(this.message(error, 'Не вдалося зберегти витрату. Результат може бути невідомим; оновіть список.'));
        this.checkAuthorization(error);
        if (error instanceof HttpErrorResponse && (error.status === 409 || error.status === 404)) { this.conflict.set(true); this.load(); }
      },
    });
  }
  private checkAuthorization(error: unknown) {
    if (!(error instanceof HttpErrorResponse)) return;
    if (error.status === 401) { this.request?.unsubscribe(); this.expenseRequest?.unsubscribe(); this.auth.invalidateSession(); return; }
    if (error.status === 403) {
      this.request?.unsubscribe(); this.expenseRequest?.unsubscribe(); this.report.set(null); this.expenses.set(null); this.editing.set(null); this.expenseForm.reset(); this.notice.set('');
      this.auth.getSession().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ error: () => {} });
    }
  }
  private message(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) return 'Сесію завершено. Увійдіть знову.';
      if (error.status === 403) return 'Доступ до фінансового звіту заборонено.';
      if (error.status === 400) return 'Перевірте суму, дати та інші введені параметри.';
      if (error.status === 422) return 'Фінансові підсумки перевищують підтримуваний діапазон. Перевірте та виправте витрати або замовлення.';
      if (error.status === 409) return 'Витрату змінено. Оберіть її з оновленого списку та повторіть виправлення.';
      if (error.status === 404) return 'Витрату не знайдено. Оновіть список.';
    }
    return fallback;
  }
}
