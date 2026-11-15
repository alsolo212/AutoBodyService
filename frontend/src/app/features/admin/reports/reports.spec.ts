import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../../../core/auth';
import { Expense, ExpensePage, FinanceReport } from './reports-api';
import { formatUah, Reports } from './reports';

const session = { name: 'Test', phone: '+380000000099', roles: ['SuperAdmin'], mustChangePassword: false };
const order = { id: 'order-1', make: 'Test', model: 'Model', completedAtUtc: '2026-09-30T21:00:00Z', amountExact: '9007199254740993.125' };
const expense: Expense = { id: 'expense-1', amountExact: '10.25', description: 'Матеріали', recordedAtUtc: '2026-09-30T20:59:59Z', recordedYear: 2026, recordedMonth: 9, updatedAtUtc: null, version: 10 };
const report: FinanceReport = { year: 2026, month: 9, completedCount: 1, revenueExact: '9007199254740993.125', expensesExact: '10.25', resultExact: '-10.25', filteredCount: 1, page: 1, pageSize: 10, orders: [order] };
const expenses: ExpensePage = { year: 2026, month: 9, total: 1, page: 1, pageSize: 10, items: [expense] };

describe('Reports', () => {
  let http: HttpTestingController; let auth: AuthService;
  beforeEach(() => {
    sessionStorage.removeItem('reports-view');
    TestBed.configureTestingModule({ imports: [Reports], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService); auth.session.set(session);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  });
  afterEach(() => http.verify());
  function reportRequest() { return http.expectOne(req => req.url === '/api/admin/reports/finance'); }
  function expensesRequest() { return http.expectOne(req => req.url === '/api/admin/reports/expenses'); }
  function flushData(r = report, e = expenses) { reportRequest().flush(r); expensesRequest().flush(e); }
  function create() { const fixture = TestBed.createComponent(Reports); fixture.detectChanges(); flushData(); fixture.detectChanges(); return fixture; }
  it('preserves tab and month while switching and returning to reports', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.periodForm.setValue({ year: 2020, month: 9 }); component.applyPeriod(); flushData();
    component.selectTab('audit'); fixture.detectChanges();
    http.expectOne(req => req.url === '/api/admin/audit-log').flush({ total: 0, page: 1, pageSize: 10, items: [] });
    component.selectTab('finance'); fixture.detectChanges();
    expect(component.period()).toEqual({ year: 2020, month: 9 });
    component.selectTab('audit'); fixture.destroy();
    const restored = TestBed.createComponent(Reports); restored.detectChanges();
    const reportQuery = reportRequest(); expect(reportQuery.request.params.get('year')).toBe('2020'); expect(reportQuery.request.params.get('month')).toBe('9');
    reportQuery.flush(report); expensesRequest().flush(expenses);
    http.expectOne(req => req.url === '/api/admin/audit-log').flush({ total: 0, page: 1, pageSize: 10, items: [] });
    expect(restored.componentInstance.activeTab()).toBe('audit');
  });
  it('formats precise UAH values without converting to floating point', () => {
    expect(formatUah('0')).toBe('0,00 ₴'); expect(formatUah('-10.25')).toBe('−10,25 ₴');
    expect(formatUah('9007199254740993.125')).toBe('9\u00a0007\u00a0199\u00a0254\u00a0740\u00a0993,125 ₴');
    const fixture = create();
    expect(fixture.nativeElement.textContent).toContain('9\u00a0007\u00a0199\u00a0254\u00a0740\u00a0993,125 ₴');
    expect(fixture.nativeElement.querySelector('a[href="/admin/appointments/order-1"]')).not.toBeNull();
  });
  it('rejects non-integer years and non-positive or invalid expenses before sending', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.periodForm.setValue({ year: 2020.5, month: 9 }); component.applyPeriod();
    expect(component.periodForm.invalid).toBe(true); http.expectNone(req => req.url === '/api/admin/reports/finance');
    component.expenseForm.setValue({ amount: '0', description: '' }); component.save();
    expect(component.expenseForm.controls.amount.invalid).toBe(true); http.expectNone('/api/auth/antiforgery');
    component.expenseForm.setValue({ amount: '1e9', description: '' }); component.save();
    expect(component.expenseForm.controls.amount.invalid).toBe(true); http.expectNone('/api/auth/antiforgery');
  });
  it('queries selected month and server-side date and amount filters with sorting and paging', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.periodForm.setValue({ year: 2026, month: 10 }); component.applyPeriod();
    let query = reportRequest(); expect(query.request.params.get('month')).toBe('10'); query.flush({ ...report, year: 2026, month: 10, orders: [] });
    expensesRequest().flush({ ...expenses, month: 10, items: [], total: 0 }); fixture.detectChanges();
    component.filterForm.setValue({ fromDate: '2026-10-01', toDate: '2026-10-31', minAmount: '0,10', maxAmount: '9007199254740993,125', sort: 'amount', direction: 'asc' });
    component.applyFilters(); query = reportRequest();
    expect(query.request.params.get('minAmount')).toBe('0.1'); expect(query.request.params.get('maxAmount')).toBe('9007199254740993.125');
    expect(query.request.params.get('fromDate')).toBe('2026-10-01'); expect(query.request.params.get('sort')).toBe('amount');
    query.flush({ ...report, filteredCount: 11 }); expensesRequest().flush(expenses); fixture.detectChanges();
    component.changeOrderPage(1); query = reportRequest(); expect(query.request.params.get('page')).toBe('2');
    query.flush({ ...report, page: 2, filteredCount: 11 }); expensesRequest().flush(expenses);
  });
  it('records a new expense without a selected month and reports the server month', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.periodForm.setValue({ year: 2020, month: 2 }); component.applyPeriod(); flushData({ ...report, year: 2020, month: 2, orders: [] }, { ...expenses, year: 2020, month: 2, items: [], total: 0 });
    component.expenseForm.setValue({ amount: '9007199254740993,125', description: 'Матеріали' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = expensesRequest(); expect(request.request.method).toBe('POST');
    expect(request.request.body).toBe('{"description":"Матеріали","amount":9007199254740993.125}');
    request.flush({ ...expense, recordedYear: 2026, recordedMonth: 11 }); flushData(); fixture.detectChanges();
    expect(component.notice()).toContain('листопад 2026');
    expect(component.notice()).toContain('часом сервера');
  });
  it('corrects an old expense with its version and keeps the original period', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.edit(expense); component.expenseForm.setValue({ amount: '12,34', description: '' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/reports/expenses/expense-1');
    expect(request.request.method).toBe('PATCH'); expect(request.request.body).toBe('{"version":10,"description":null,"amount":12.34}');
    request.flush({ ...expense, amountExact: '12.34', description: null, version: 11 }); flushData(); fixture.detectChanges();
    expect(component.notice()).toContain('залишається за вересень 2026');
    expect(component.editing()).toBeNull();
  });
  it('retains the draft and requires a new selection after a conflict', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.edit(expense); component.expenseForm.patchValue({ amount: '15' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/reports/expenses/expense-1').flush(null, { status: 409, statusText: 'Conflict' });
    flushData(report, { ...expenses, items: [{ ...expense, version: 11 }] }); fixture.detectChanges();
    expect(component.conflict()).toBe(true); expect(component.expenseForm.controls.amount.value).toBe('15');
    expect(component.actionError()).toContain('Оберіть її з оновленого списку');
    component.save(); http.expectNone('/api/auth/antiforgery');
  });
  it('keeps filter controls usable after a 400 and presents empty monthly totals', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.filterForm.patchValue({ fromDate: '2025-01-01' }); component.applyFilters();
    reportRequest().flush(null, { status: 400, statusText: 'Bad Request' });
    const other = expensesRequest(); expect(other.cancelled).toBe(true); fixture.detectChanges();
    expect(component.loadError()).toContain('Перевірте суму');
    expect(fixture.nativeElement.querySelector('#order-from')).not.toBeNull();
    component.filterForm.patchValue({ fromDate: '' }); component.applyFilters();
    flushData({ ...report, completedCount: 0, revenueExact: '0', expensesExact: '0', resultExact: '0', filteredCount: 0, orders: [] }, { ...expenses, total: 0, items: [] }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('0,00 ₴');
    expect(fixture.nativeElement.textContent).toContain('витрат немає');
  });
  it('clears financial data after authorization loss and ignores a late response', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.load(); const oldReport = reportRequest(); const oldExpenses = expensesRequest();
    auth.invalidateSession(); oldReport.flush(report); oldExpenses.flush(expenses); fixture.detectChanges();
    expect(component.report()).toBeNull(); expect(component.expenses()).toBeNull();
    expect(TestBed.inject(Router).navigateByUrl).toHaveBeenCalledWith('/login');
  });
  it('reports a forbidden response and rechecks the current session', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.load(); reportRequest().flush(null, { status: 403, statusText: 'Forbidden' });
    const cancelled = expensesRequest(); expect(cancelled.cancelled).toBe(true);
    http.expectOne('/api/auth/session').flush(session); fixture.detectChanges();
    expect(component.loadError()).toContain('заборонено'); expect(component.report()).toBeNull();
  });
  it('retains expense input when the server rejects it with 400', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.expenseForm.setValue({ amount: '12,34', description: 'Матеріали' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    expensesRequest().flush(null, { status: 400, statusText: 'Bad Request' }); fixture.detectChanges();
    expect(component.actionError()).toContain('Перевірте суму');
    expect(component.expenseForm.getRawValue()).toEqual({ amount: '12,34', description: 'Матеріали' });
  });
  it('invalidates and redirects an unauthorized session on loading', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.load(); reportRequest().flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(expensesRequest().cancelled).toBe(true); fixture.detectChanges();
    expect(auth.session()).toBeNull(); expect(component.report()).toBeNull();
    expect(TestBed.inject(Router).navigateByUrl).toHaveBeenCalledWith('/login');
  });
});
