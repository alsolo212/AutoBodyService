import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Dashboard } from './dashboard';
import { AdminDashboard } from '../appointment-details/admin-appointment-api';
import { ManualContactDialog } from '../manual-contact-dialog/manual-contact-dialog';
import { By } from '@angular/platform-browser';
import { vi } from 'vitest';

const item = { id: 'one', customerName: 'Клієнт', make: 'Тест', model: 'Авто', status: 'Scheduled' as const,
  inspectionStartUtc: '2026-07-01T09:00:00Z', inspectionStartLocal: '2026-07-01T12:00:00+03:00', occurredAtUtc: '2026-01-01T09:00:00Z' };
const task = { appointmentId: 'one', customerName: 'Клієнт', make: 'Тест', model: 'Авто', reason: 'Agreement' as const, triggeredAtUtc: '2026-01-01T09:00:00Z' };
const dashboard: AdminDashboard = { counts: { awaitingInspection: 1, scheduled: 2, inProgress: 3, awaitingPayment: 4 }, upcomingInspections: [item], newAppointments: [item], cancellations: [item], contactTasks: [task] };
const details = { ...item, customerPhone: '+380000000001', version: 41, manualContacts: [], history: [], repairPlan: null, assignments: null, participants: null };

describe('Dashboard', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [Dashboard], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function create(data = dashboard) {
    const fixture = TestBed.createComponent(Dashboard); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо огляд');
    http.expectOne('/api/admin/dashboard').flush(data); fixture.detectChanges();
    http.expectOne('/api/admin/repair-planning').flush({ conflicts: [], earlyOpportunity: null }); fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    dialog.showModal = vi.fn(); dialog.close = vi.fn();
    return fixture;
  }
  it('loads one real dashboard, renders all four counts, events, Kyiv times and protected links without financial fields', () => {
    const fixture = create(); const element = fixture.nativeElement as HTMLElement;
    const numbers = [...element.querySelectorAll('.summary-grid strong')].map(node => node.textContent?.trim());
    expect(numbers).toEqual(['1', '2', '3', '4']);
    expect([...element.querySelectorAll<HTMLAnchorElement>('.summary-grid a')].map(link => link.getAttribute('href'))).toEqual([
      '/admin/appointments?status=AwaitingInspection', '/admin/appointments?status=Scheduled',
      '/admin/appointments?status=InProgress', '/admin/appointments?status=AwaitingPayment',
    ]);
    for (const text of ['Найближчі огляди', 'Нові записи', 'Скасування', 'Ручний зв’язок', 'Клієнт', 'Тест Авто', '12:00', '11:00']) expect(element.textContent).toContain(text);
    for (const url of ['/admin/appointments', '/admin/appointments/one', '/admin/inspection-calendar', '/admin/assignments', '/admin/employees', '/admin/prices']) expect(element.querySelector(`a[href="${url}"]`)).not.toBeNull();
    expect(element.textContent).not.toMatch(/фінанс|прибут|витрат|аудит/i);
    http.expectNone('/api/admin/dashboard');
  });
  it('renders truthful empty states for every event list', () => {
    const fixture = create({ ...dashboard, upcomingInspections: [], newAppointments: [], cancellations: [], contactTasks: [] });
    for (const text of ['Майбутніх оглядів немає', 'Нових записів немає', 'Скасувань немає', 'Наразі немає завдань']) expect(fixture.nativeElement.textContent).toContain(text);
  });
  it('records the selected manual contact once and reloads authoritative tasks after success', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.markContact(task);
    http.expectOne('/api/appointments/one').flush(details);
    const dialog = fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog;
    dialog.confirm(); dialog.confirm();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/appointments/one/manual-contact');
    expect(request.request.body).toEqual({ reason: 'Agreement', version: 41, customerPhone: details.customerPhone, customerName: details.customerName });
    expect(component.data()?.contactTasks).toEqual([task]);
    request.flush(null);
    http.expectOne('/api/admin/dashboard').flush({ ...dashboard, contactTasks: [] }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Ручний контакт відмічено');
    expect(component.data()?.contactTasks).toEqual([]);
    http.expectNone('/api/admin/dashboard');
  });
  it('retains a task on save failure and reports forbidden or failed loads with retry', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.markContact(task); http.expectOne('/api/appointments/one').flush(details);
    (fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog).confirm(); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/appointments/one/manual-contact').flush(null, { status: 403, statusText: 'Forbidden' });
    expect((fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog).error()).toContain('Не вдалося'); expect(component.data()?.contactTasks).toEqual([task]);
    component.reload(); http.expectOne('/api/admin/dashboard').flush(null, { status: 403, statusText: 'Forbidden' }); fixture.detectChanges();
    expect(component.data()).toBeNull(); expect(fixture.nativeElement.textContent).toContain('Доступ до панелі заборонено');
    component.reload(); http.expectOne('/api/admin/dashboard').flush(null, { status: 500, statusText: 'Error' });
    expect(component.error()).toContain('Не вдалося завантажити');
    component.reload(); http.expectOne('/api/admin/dashboard').flush(dashboard); expect(component.error()).toBe('');
  });
  it('keeps the task and reports a stale snapshot without retrying contact', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.markContact(task); http.expectOne('/api/appointments/one').flush(details);
    const dialog = fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog;
    dialog.confirm(); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/appointments/one/manual-contact').flush(null, { status: 409, statusText: 'Conflict' });
    expect(dialog.error()).toContain('змінилися');
    expect(component.notice()).toBe('');
    expect(component.data()?.contactTasks).toEqual([task]);
    http.expectNone('/api/admin/appointments/one/manual-contact');
  });
  it('closing a contact dialog never records contact', () => {
    const fixture = create(); fixture.componentInstance.markContact(task);
    http.expectOne('/api/appointments/one').flush(details);
    const dialog = fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog;
    dialog.close(); dialog.closed();
    expect(fixture.componentInstance.data()?.contactTasks).toEqual([task]);
    http.expectNone('/api/auth/antiforgery');
  });
  it('blocks a task when the current order or customer no longer matches', () => {
    const fixture = create(); fixture.componentInstance.markContact(task);
    http.expectOne('/api/appointments/one').flush({ ...details, status: 'InProgress' }); fixture.detectChanges();
    const dialog = fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog;
    expect(dialog.error()).toContain('Стан замовлення змінився');
    dialog.confirm(); http.expectNone('/api/auth/antiforgery');
    dialog.closed(); fixture.componentInstance.markContact(task);
    http.expectOne('/api/appointments/one').flush({ ...details, customerName: 'Інший клієнт' });
    expect(dialog.error()).toContain('Дані клієнта');
    dialog.confirm(); http.expectNone('/api/auth/antiforgery');
  });
  it('does not offer a contact confirmation without a customer phone', () => {
    const fixture = create(); fixture.componentInstance.markContact(task);
    http.expectOne('/api/appointments/one').flush({ ...details, customerPhone: '' });
    const dialog = fixture.debugElement.query(By.directive(ManualContactDialog)).componentInstance as ManualContactDialog;
    expect(dialog.error()).toContain('Телефон недоступний');
    expect(dialog.item()).toBeNull(); dialog.confirm();
    http.expectNone('/api/auth/antiforgery');
  });
});
