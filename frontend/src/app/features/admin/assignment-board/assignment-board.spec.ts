import { CdkDragDrop } from '@angular/cdk/drag-drop';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminBoardCard } from '../appointment-details/admin-appointment-api';
import { ActiveAssignment, Employee } from '../employees/employees-api';
import { AssignmentBoard } from './assignment-board';

const car: AdminBoardCard = { id: 'order-1', make: 'Test', model: 'Car', customerName: 'Client', vin: 'VIN', status: 'InProgress', inspectionStartUtc: '2099-01-01T00:00:00Z', inspectionStartLocal: '2099-01-01T02:00:00+02:00', repairPlan: null, version: 1 };
const first: Employee = { id: 'worker-1', fullName: 'Один', specialization: 'Маляр', isActive: true, version: 1 };
const second: Employee = { id: 'worker-2', fullName: 'Два', specialization: 'Рихтувальник', isActive: true, version: 1 };
const inactive: Employee = { ...second, id: 'inactive', isActive: false };
const one: ActiveAssignment = { id: 'assignment-1', appointmentId: car.id, employeeId: first.id };
const two: ActiveAssignment = { id: 'assignment-2', appointmentId: car.id, employeeId: second.id };

describe('AssignmentBoard', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [AssignmentBoard], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function flush(orders: AdminBoardCard[] = [car], employees: Employee[] = [first, second, inactive], assignments: ActiveAssignment[] = []) {
    const request = http.expectOne(req => req.url === '/api/admin/appointments' && req.params.get('page') === '1');
    expect(request.request.params.get('status')).toBe('InProgress');
    request.flush({ items: orders, page: 1, pageSize: 100, total: orders.length });
    http.expectOne('/api/admin/employees').flush(employees);
    http.expectOne('/api/admin/assignments?active=true').flush(assignments);
  }
  function create(assignments: ActiveAssignment[] = []) { const fixture = TestBed.createComponent(AssignmentBoard); fixture.detectChanges(); flush([car], [first, second, inactive], assignments); fixture.detectChanges(); return fixture; }
  it('loads all pages, keeps assigned orders in the source and hides inactive workers', () => {
    const fixture = TestBed.createComponent(AssignmentBoard); fixture.detectChanges();
    http.expectOne(req => req.url === '/api/admin/appointments' && req.params.get('page') === '1').flush({ items: [car], page: 1, pageSize: 100, total: 101 });
    http.expectOne(req => req.url === '/api/admin/appointments' && req.params.get('page') === '2').flush({ items: [{ ...car, id: 'order-2' }], page: 2, pageSize: 100, total: 101 });
    http.expectOne('/api/admin/employees').flush([first, second, inactive]);
    http.expectOne('/api/admin/assignments?active=true').flush([one, two]); fixture.detectChanges();
    expect(fixture.componentInstance.orders().map(o => o.id)).toEqual(['order-1', 'order-2']);
    expect(fixture.nativeElement.querySelectorAll('#assignment-source .order-card').length).toBe(2);
    expect(fixture.nativeElement.querySelectorAll('.worker').length).toBe(2);
    expect(fixture.nativeElement.querySelectorAll('.assigned-card').length).toBe(2);
    expect(fixture.componentInstance.canAssign(car, first.id)).toBe(false);
    expect(fixture.componentInstance.canAssign(car, inactive.id)).toBe(false);
    expect(fixture.componentInstance.canAssign({ ...car, status: 'Scheduled' }, first.id)).toBe(false);
  });
  it('keyboard assignment waits for success and reloads authoritative data', () => {
    const fixture = create([one]); const component = fixture.componentInstance;
    component.select(second.id, car.id); component.assignSelected(second);
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/assignments');
    expect(request.request.body).toEqual({ appointmentId: car.id, employeeId: second.id });
    expect(component.assignments()).toEqual([one]);
    request.flush(two); expect(component.assignments()).toEqual([one]);
    flush([car], [first, second], [one, two]); fixture.detectChanges();
    expect(component.assignments()).toEqual([one, two]); expect(component.orders()).toEqual([car]);
    expect(fixture.nativeElement.querySelectorAll('.assigned-card').length).toBe(2);
  });
  it('drag copies only a valid source order and ignores invalid drops', () => {
    const fixture = create(); const component = fixture.componentInstance;
    const drop = { previousContainer: { id: 'assignment-source' }, container: { data: first }, item: { data: car }, isPointerOverContainer: true } as unknown as CdkDragDrop<Employee, AdminBoardCard[], AdminBoardCard>;
    component.dropped({ ...drop, isPointerOverContainer: false }); component.dropped({ ...drop, previousContainer: { id: 'other' } } as typeof drop);
    component.dropped({ ...drop, container: { data: inactive } } as typeof drop);
    http.expectNone('/api/auth/antiforgery');
    component.dropped(drop); http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/assignments'); expect(request.request.body).toEqual({ appointmentId: car.id, employeeId: first.id });
    request.flush(one); flush([car], [first, second], [one]);
    expect(component.orders()).toEqual([car]); expect(component.assignments()).toEqual([one]);
  });
  it('releases only the selected assignment and refreshes without changing the source order', () => {
    const fixture = create([one, two]); const component = fixture.componentInstance;
    component.release(one); http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/assignments/assignment-1/release');
    expect(request.request.method).toBe('PATCH'); expect(component.assignments()).toEqual([one, two]);
    request.flush(null); flush([car], [first, second], [two]); fixture.detectChanges();
    expect(component.assignments()).toEqual([two]); expect(component.orders()).toEqual([car]);
    expect(fixture.nativeElement.querySelectorAll('.assigned-card').length).toBe(1);
  });
  it('reports conflicts without optimistic changes and reloads server state', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.assign(car, first); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/assignments').flush(null, { status: 409, statusText: 'Conflict' });
    expect(component.assignments()).toEqual([]); expect(component.actionError()).toContain('Дані змінилися');
    flush([car], [first, second], [one]); fixture.detectChanges();
    expect(component.assignments()).toEqual([one]); expect(fixture.nativeElement.textContent).toContain('Дані змінилися');
  });
  it('keeps failed mutations unchanged and hides stale data when the refresh fails', () => {
    const fixture = create([one]); const component = fixture.componentInstance;
    component.release(one); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/assignments/assignment-1/release').flush(null, { status: 500, statusText: 'Error' });
    expect(component.assignments()).toEqual([one]); expect(component.loading()).toBe(true);
    expect(component.actionError()).toContain('Не вдалося завершити призначення');
    const orders = http.expectOne(req => req.url === '/api/admin/appointments');
    const employees = http.expectOne('/api/admin/employees'); const assignments = http.expectOne('/api/admin/assignments?active=true');
    orders.flush(null, { status: 500, statusText: 'Error' }); expect(employees.cancelled).toBe(true); expect(assignments.cancelled).toBe(true); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.worker')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Не вдалося завантажити розподіл робіт');
  });
  it('shows forbidden and empty states and does not show stale data after a failed refresh', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.load();
    const orders = http.expectOne(req => req.url === '/api/admin/appointments');
    const employees = http.expectOne('/api/admin/employees'); const assignments = http.expectOne('/api/admin/assignments?active=true');
    orders.flush(null, { status: 403, statusText: 'Forbidden' }); expect(employees.cancelled).toBe(true); expect(assignments.cancelled).toBe(true); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Доступ до розподілу робіт заборонено');
    expect(component.orders()).toEqual([]);
    component.load(); flush([], [], []); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Замовлень у роботі немає');
    expect(fixture.nativeElement.textContent).toContain('Активних працівників немає');
  });
});
