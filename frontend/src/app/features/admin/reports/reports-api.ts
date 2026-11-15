import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export interface CompletedOrder { id: string; make: string; model: string; completedAtUtc: string; amountExact: string; }
export interface FinanceReport { year: number; month: number; completedCount: number; revenueExact: string; expensesExact: string; resultExact: string; filteredCount: number; page: number; pageSize: number; orders: CompletedOrder[]; }
export interface Expense { id: string; amountExact: string; description: string | null; recordedAtUtc: string; recordedYear: number; recordedMonth: number; updatedAtUtc: string | null; version: number; }
export interface ExpensePage { year: number; month: number; total: number; page: number; pageSize: number; items: Expense[]; }
export interface ReportQuery { year: number; month: number; page: number; sort: string; direction: string; fromDate: string; toDate: string; minAmount: string; maxAmount: string; }

@Injectable({ providedIn: 'root' })
export class ReportsApi {
  private readonly http = inject(HttpClient);
  private readonly root = '/api/admin/reports';
  report(query: ReportQuery) {
    let params = new HttpParams().set('year', query.year).set('month', query.month).set('page', query.page).set('pageSize', 10).set('sort', query.sort).set('direction', query.direction);
    for (const key of ['fromDate', 'toDate', 'minAmount', 'maxAmount'] as const) if (query[key]) params = params.set(key, query[key]);
    return this.http.get<FinanceReport>(`${this.root}/finance`, { params });
  }
  expenses(year: number, month: number, sort: string, direction: string, page: number) {
    const params = new HttpParams().set('year', year).set('month', month).set('sort', sort).set('direction', direction).set('page', page).set('pageSize', 10);
    return this.http.get<ExpensePage>(`${this.root}/expenses`, { params });
  }
  save(amount: string, description: string | null, expense: Expense | null) {
    // The numeric literal is validated before this call; no binary floating-point conversion.
    const fields = JSON.stringify(expense ? { version: expense.version, description } : { description });
    const body = fields.slice(0, -1) + ',"amount":' + amount + '}';
    const headers = new HttpHeaders({ 'Content-Type': 'application/json' });
    return this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => expense
      ? this.http.patch<Expense>(`${this.root}/expenses/${encodeURIComponent(expense.id)}`, body, { headers })
      : this.http.post<Expense>(`${this.root}/expenses`, body, { headers })));
  }
}
