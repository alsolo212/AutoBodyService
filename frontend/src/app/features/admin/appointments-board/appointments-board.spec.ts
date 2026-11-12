import { CdkDrag, CdkDropList } from '@angular/cdk/drag-drop';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AppointmentStatus } from '../../appointments/appointments-api';
import { AdminBoardCard } from '../appointment-details/admin-appointment-api';
import { AppointmentsBoard } from './appointments-board';

const statuses: AppointmentStatus[] = ['AwaitingInspection', 'Scheduled', 'InProgress', 'AwaitingPayment', 'Completed', 'Cancelled'];
const card = (status: AppointmentStatus, id = status): AdminBoardCard => ({ id, customerName: 'Клієнт', make: 'Марка', model: 'Авто', vin: '1HGCM82633A123456', inspectionStartUtc: '2026-01-01T10:00:00Z', inspectionStartLocal: '2026-01-01T12:00:00+02:00', status, repairPlan: null, version: 42 });

describe('administrative board', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [AppointmentsBoard], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => { http.verify(); vi.restoreAllMocks(); });
  function flushPages(cards: AdminBoardCard[] = [], total = 0) {
    const requests = http.match(r => r.url === '/api/admin/appointments');
    expect(requests.length).toBeGreaterThan(0);
    for (const request of requests) {
      const status = request.request.params.get('status') as AppointmentStatus;
      const page = Number(request.request.params.get('page'));
      const pageSize = Number(request.request.params.get('pageSize'));
      const items = cards.filter(c => c.status === status);
      request.flush({ items, page, pageSize, total: Math.max(total, items.length) });
    }
    return requests;
  }
  function create(cards: AdminBoardCard[] = [], total = 0) {
    const fixture = TestBed.createComponent(AppointmentsBoard); fixture.detectChanges(); flushPages(cards, total); fixture.detectChanges(); return fixture;
  }
  function drop(component: AppointmentsBoard, item: AdminBoardCard, from: AppointmentStatus, to: AppointmentStatus) {
    const columns = component.columns();
    const source = { data: columns.find(c => c.status === from)! } as CdkDropList;
    const target = { data: columns.find(c => c.status === to)! } as CdkDropList;
    const drag = { data: item } as CdkDrag<AdminBoardCard>;
    component.dropped({ previousContainer: source, container: target, item: drag, isPointerOverContainer: true } as unknown as Parameters<AppointmentsBoard['dropped']>[0]);
  }

  it('loads six separate server pages and displays plan and terminal read-only cards', () => {
    const ready = { ...card('Scheduled'), repairPlan: { agreedPrice: 200, currency: 'UAH', plannedStartUtc: '2026-01-01T10:00:00Z', estimatedFinishUtc: '2026-01-02T10:00:00Z' } };
    const fixture = create([ready, card('Completed'), card('Cancelled')]);
    expect(fixture.componentInstance.columns().map(c => c.status)).toEqual(statuses);
    const text = fixture.nativeElement.textContent as string;
    for (const s of ['Очікує огляду', 'Заплановано', 'У роботі', 'Очікує оплати', 'Завершено', 'Скасовано', 'Клієнт', '1HGCM82633A123456', '₴', 'UTC+02:00']) expect(text).toContain(s);
    for (const status of ['Completed', 'Cancelled']) {
      const article = fixture.nativeElement.querySelector(`[data-card-id="${status}"]`) as HTMLElement;
      expect(article.querySelector('button')).toBeNull(); expect(article.querySelector('summary')).toBeNull();
    }
  });

  it('allows drag only to the next status and excludes cancellation and terminal sources', () => {
    const fixture = create(); const component = fixture.componentInstance;
    for (const from of statuses) for (const to of statuses) {
      const drag = { data: card(from) } as CdkDrag<AdminBoardCard>;
      const column = { data: component.columns().find(c => c.status === to)! } as CdkDropList;
      expect(component.canEnter(drag, column)).toBe(component.next(from) === to);
    }
    drop(component, card('Scheduled'), 'Scheduled', 'Completed');
    http.expectNone('/api/auth/antiforgery');
  });

  it('sends the displayed version and keeps the original card until the server responds', () => {
    const fixture = create([card('Scheduled')]); const component = fixture.componentInstance;
    drop(component, card('Scheduled'), 'Scheduled', 'InProgress');
    expect(component.columns().find(c => c.status === 'Scheduled')!.items).toHaveLength(1);
    expect(component.columns().find(c => c.status === 'InProgress')!.items).toHaveLength(0);
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/appointments/Scheduled/transition');
    expect(request.request.body).toEqual({ version: 42, target: 'InProgress' });
    expect(component.columns().find(c => c.status === 'Scheduled')!.items).toHaveLength(1);
    request.flush(null); flushPages([card('InProgress', 'Scheduled')]); fixture.detectChanges();
    expect(component.columns().find(c => c.status === 'Scheduled')!.items).toHaveLength(0);
    expect(component.columns().find(c => c.status === 'InProgress')!.items).toHaveLength(1);
  });

  it('reloads authoritative columns after 409 without retrying the transition', () => {
    const fixture = create([card('Scheduled')]); const component = fixture.componentInstance;
    component.advance(card('Scheduled')); http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/appointments/Scheduled/transition');
    request.flush(null, { status: 409, statusText: 'Conflict' });
    expect(component.actionMessage()).toContain('Замовлення змінилося');
    flushPages([card('InProgress', 'Scheduled')]); fixture.detectChanges();
    expect(component.columns().find(c => c.status === 'Scheduled')!.items).toHaveLength(0);
    expect(component.columns().find(c => c.status === 'InProgress')!.items[0].id).toBe('Scheduled');
    http.expectNone('/api/admin/appointments/Scheduled/transition');
  });

  it('routes drag and button actions to the same existing confirmation forms', () => {
    const fixture = create(); const component = fixture.componentInstance;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const initial = card('AwaitingInspection');
    component.advance(initial); drop(component, initial, 'AwaitingInspection', 'Scheduled');
    expect(navigate).toHaveBeenNthCalledWith(1, ['/admin/appointments', initial.id], { queryParams: { action: 'plan', version: 42 } });
    expect(navigate).toHaveBeenNthCalledWith(2, ['/admin/appointments', initial.id], { queryParams: { action: 'plan', version: 42 } });
    const payment = card('AwaitingPayment'); component.advance(payment); drop(component, payment, 'AwaitingPayment', 'Completed');
    expect(navigate).toHaveBeenNthCalledWith(3, ['/admin/appointments', payment.id], { queryParams: { action: 'complete', version: 42 } });
    expect(navigate).toHaveBeenNthCalledWith(4, ['/admin/appointments', payment.id], { queryParams: { action: 'complete', version: 42 } });
    component.cancel(card('Scheduled')); expect(navigate).toHaveBeenLastCalledWith(['/admin/appointments', 'Scheduled'], { queryParams: { action: 'cancel', version: 42 } });
    const calls = navigate.mock.calls.length; component.cancel(card('AwaitingPayment')); expect(navigate).toHaveBeenCalledTimes(calls);
  });

  it('applies server filters and independent column pagination', () => {
    const fixture = create([], 13); const component = fixture.componentInstance;
    component.changePage('Scheduled', 1); const pages = http.match(r => r.url === '/api/admin/appointments');
    expect(pages).toHaveLength(6);
    for (const req of pages) { const status = req.request.params.get('status'); expect(req.request.params.get('page')).toBe(status === 'Scheduled' ? '2' : '1'); req.flush({ items: [], page: status === 'Scheduled' ? 2 : 1, pageSize: 5, total: 13 }); }
    component.filterForm.patchValue({ search: 'VIN', status: 'Completed', from: '2026-01-01', to: '2026-12-31', sort: 'created', direction: 'desc', pageSize: 10 });
    component.applyFilters(); const filtered = http.expectOne(r => r.url === '/api/admin/appointments');
    expect(filtered.request.params.get('status')).toBe('Completed'); expect(filtered.request.params.get('search')).toBe('VIN');
    expect(filtered.request.params.get('from')).toBe('2026-01-01'); expect(filtered.request.params.get('to')).toBe('2026-12-31');
    expect(filtered.request.params.get('sort')).toBe('created'); expect(filtered.request.params.get('direction')).toBe('desc'); expect(filtered.request.params.get('pageSize')).toBe('10');
    filtered.flush({ items: [card('Completed')], page: 1, pageSize: 10, total: 1 }); fixture.detectChanges();
    expect(component.columns().filter(c => c.filtered)).toHaveLength(5); expect(component.columns().find(c => c.status === 'Completed')!.items).toHaveLength(1);
    component.filterForm.patchValue({ from: '2026-12-31', to: '2026-01-01' }); component.applyFilters();
    expect(component.error()).toContain('діапазон дат'); http.expectNone(r => r.url === '/api/admin/appointments');
  });

  it('shows an error and no stale cards when loading is forbidden', () => {
    const fixture = TestBed.createComponent(AppointmentsBoard); fixture.detectChanges();
    const requests = http.match(r => r.url === '/api/admin/appointments'); requests[0].flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges(); expect(fixture.componentInstance.error()).toContain('заборонено');
    expect(fixture.componentInstance.columns().every(c => c.items.length === 0)).toBe(true);
  });
});
