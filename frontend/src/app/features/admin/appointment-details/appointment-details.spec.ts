import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { vi } from 'vitest';
import { AppointmentDetailsAdmin } from './appointment-details';
import { AdminAppointment } from './admin-appointment-api';
import { kyivInput, kyivLocalToUtc, kyivWithOffset } from './kyiv-date';

const base: AdminAppointment = {
  id: 'order-1', vin: '1HGCM82633A123456', make: 'Тест', model: 'Авто', problemDescription: 'Пошкоджено двері',
  customerName: 'Клієнт', customerPhone: '+380000000001', inspectionStartUtc: '2026-10-25T00:30:00Z', status: 'AwaitingInspection', version: 41,
  history: [{ fromStatus: null, toStatus: 'AwaitingInspection', changedAtUtc: '2026-01-01T10:00:00Z', actorName: 'Клієнт', reason: null }],
  repairPlan: null, participants: null, assignments: null, manualContacts: [], paymentConfirmation: null,
};
const plan = { agreedPrice: 1250.5, currency: 'UAH', plannedStartUtc: '2026-10-25T00:30:00Z', estimatedFinishUtc: '2026-10-25T01:30:00Z' };

describe('Kyiv date conversion', () => {
  it('distinguishes both autumn instants and rejects spring gaps and wrong offsets', () => {
    expect(kyivLocalToUtc('2026-10-25T03:30', '+03:00')).toBe('2026-10-25T00:30:00.000Z');
    expect(kyivLocalToUtc('2026-10-25T03:30', '+02:00')).toBe('2026-10-25T01:30:00.000Z');
    expect(kyivLocalToUtc('2026-03-29T03:30', '+02:00')).toBeNull();
    expect(kyivLocalToUtc('2026-03-29T03:30', '+03:00')).toBeNull();
    expect(kyivLocalToUtc('2026-07-01T12:00', '+02:00')).toBeNull();
    expect(kyivInput('2026-10-25T01:30:00Z')).toEqual({ local: '2026-10-25T03:30:00', offset: '+02:00' });
    expect(kyivWithOffset('2026-10-25T00:30:00Z')).toContain('UTC+03:00');
    expect(kyivWithOffset('2026-10-25T01:30:00Z')).toContain('UTC+02:00');
  });
});

describe('administrative appointment details', () => {
  let http: HttpTestingController;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let snapshot: { queryParamMap: ReturnType<typeof convertToParamMap> };
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({ id: 'order-1' }));
    snapshot = { queryParamMap: convertToParamMap({}) };
    TestBed.configureTestingModule({ imports: [AppointmentDetailsAdmin], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { paramMap: params, snapshot } }] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => { http.verify(); vi.unstubAllGlobals(); });
  function create(item: AdminAppointment = base) {
    const fixture = TestBed.createComponent(AppointmentDetailsAdmin); fixture.detectChanges();
    http.expectOne('/api/appointments/order-1').flush(item); fixture.detectChanges();
    return fixture;
  }
  function token() { http.expectOne('/api/auth/antiforgery').flush(null); }

  it('refuses a stale board action while displaying the current order', () => {
    snapshot.queryParamMap = convertToParamMap({ action: 'plan', version: '40' });
    const fixture = create();
    expect(fixture.componentInstance.conflict()).toBe(true);
    expect(fixture.componentInstance.editingPlan()).toBe(false);
    expect(fixture.componentInstance.item()?.version).toBe(41);
    http.expectNone('/api/auth/antiforgery');
  });

  it('renders the administrative details, complete history and offset without inventing assignments', () => {
    const fixture = create({ ...base, status: 'Completed', repairPlan: plan,
      history: [{ fromStatus: 'InProgress', toStatus: 'Cancelled', changedAtUtc: '2026-10-25T01:30:00Z', actorName: 'Адміністратор', reason: 'Причина' }],
      manualContacts: [{ reason: 'Agreement', actorName: 'Оператор', recordedAtUtc: '2026-10-25T00:30:00Z' }],
      paymentConfirmation: { actorName: 'Адміністратор', paidAtUtc: '2026-10-25T01:30:00Z', handedOverAtUtc: '2026-10-25T01:30:00Z' } });
    const text = fixture.nativeElement.textContent as string;
    for (const part of ['Клієнт', '+380000000001', '1HGCM82633A123456', 'Пошкоджено двері', 'UTC+03:00', 'UTC+02:00', 'Адміністратор', 'Причина', 'Оператор', '₴', 'Оплата та видача']) expect(text).toContain(part);
    expect(text).not.toContain('Історія призначень');
    expect(fixture.nativeElement.querySelector('summary')).toBeNull();
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('validates Kyiv times and inspection before saving a plan with the version', () => {
    const fixture = create(); const component = fixture.componentInstance; component.editPlan(); fixture.detectChanges();
    component.planForm.setValue({ price: 0, start: '2026-03-29T03:30', startOffset: '+02:00', finish: '2026-03-29T05:00', finishOffset: '+03:00', inspectionPerformed: true });
    component.savePlan(); expect(component.planError()).toContain('зміщення UTC'); http.expectNone('/api/auth/antiforgery');
    component.planForm.patchValue({ start: '2026-10-25T03:30', startOffset: '+03:00', finish: '2026-10-25T03:30', finishOffset: '+02:00', inspectionPerformed: false });
    component.savePlan(); expect(component.planError()).toContain('огляд'); http.expectNone('/api/auth/antiforgery');
    component.planForm.controls.inspectionPerformed.setValue(true); component.savePlan(); token();
    const request = http.expectOne('/api/admin/appointments/order-1/repair-plan');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ version: 41, price: 0, plannedStartUtc: '2026-10-25T00:30:00.000Z', estimatedFinishUtc: '2026-10-25T01:30:00.000Z', inspectionPerformed: true });
    request.flush(null); http.expectOne('/api/appointments/order-1').flush({ ...base, repairPlan: plan, version: 42 }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Узгоджені дані збережено');
  });

  it('uses only the next transition and requires both completion facts', () => {
    const fixture = create({ ...base, status: 'AwaitingPayment', repairPlan: plan }); const component = fixture.componentInstance;
    component.advance(); expect(component.actionError()).toContain('оплату і передачу'); http.expectNone('/api/auth/antiforgery');
    component.transitionForm.controls.paymentReceived.setValue(true); component.advance(); http.expectNone('/api/auth/antiforgery');
    component.transitionForm.controls.vehicleHandedOver.setValue(true); component.advance(); token();
    const request = http.expectOne('/api/admin/appointments/order-1/transition');
    expect(request.request.body).toMatchObject({ version: 41, target: 'Completed', paymentReceived: true, vehicleHandedOver: true });
    request.flush(null); http.expectOne('/api/appointments/order-1').flush({ ...base, status: 'Completed', repairPlan: plan, version: 42 }); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('reloads on conflict without replaying the action or overwriting the plan draft', () => {
    const fixture = create({ ...base, status: 'Scheduled', repairPlan: plan }); const component = fixture.componentInstance;
    component.editPlan(); component.planForm.controls.price.setValue(4567); component.savePlan(); token();
    const pending = http.expectOne('/api/admin/appointments/order-1/repair-plan');
    expect(pending.request.body.plannedStartUtc).toBe(plan.plannedStartUtc);
    expect(pending.request.body.estimatedFinishUtc).toBe(plan.estimatedFinishUtc);
    pending.flush(null, { status: 409, statusText: 'Conflict' });
    expect(component.loading()).toBe(true);
    http.expectOne('/api/appointments/order-1').flush({ ...base, status: 'InProgress', repairPlan: plan, version: 42 }); fixture.detectChanges();
    expect(component.planForm.controls.price.value).toBe(4567);
    expect(component.item()?.version).toBe(42);
    expect(fixture.nativeElement.textContent).toContain('Актуальні дані завантажено');
    http.expectNone('/api/admin/appointments/order-1/repair-plan');
  });

  it('requires a reason in the cancellation dialog and does not auto-repeat a stale cancellation', () => {
    const fixture = create(); const component = fixture.componentInstance;
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    dialog.showModal = vi.fn(); dialog.close = vi.fn(); component.openCancel(); expect(dialog.showModal).toHaveBeenCalledOnce();
    component.cancelForm.controls.reason.setValue('   '); component.confirmCancel(); expect(component.cancelError()).toContain('причину'); http.expectNone('/api/auth/antiforgery');
    component.cancelForm.controls.reason.setValue('  Причина  '); component.confirmCancel(); expect(dialog.close).toHaveBeenCalledOnce(); token();
    const request = http.expectOne('/api/admin/appointments/order-1/cancel'); expect(request.request.body).toEqual({ version: 41, confirmed: true, reason: 'Причина' });
    request.flush(null, { status: 409, statusText: 'Conflict' }); http.expectOne('/api/appointments/order-1').flush({ ...base, version: 42 }); fixture.detectChanges();
    expect(component.cancelForm.controls.reason.value).toBe('  Причина  '); http.expectNone('/api/admin/appointments/order-1/cancel');
  });

  it('copies contact information and records a separately selected contact reason', async () => {
    const fixture = create(); const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const component = fixture.componentInstance;
    await component.copy(base.customerPhone, 'phone'); expect(writeText).toHaveBeenCalledWith(base.customerPhone);
    component.contactForm.controls.reason.setValue('ScheduleConflictOrReschedule');
    await component.copy(component.contactText(), 'text'); expect(writeText).toHaveBeenLastCalledWith(expect.stringContaining('Конфлікт або перенесення'));
    component.markContact(); token(); const request = http.expectOne('/api/admin/appointments/order-1/manual-contact');
    expect(request.request.body).toEqual({ reason: 'ScheduleConflictOrReschedule' }); request.flush(null);
    http.expectOne('/api/appointments/order-1').flush({ ...base, manualContacts: [{ reason: 'ScheduleConflictOrReschedule', actorName: 'Оператор', recordedAtUtc: '2026-01-01T10:00:00Z' }] });
    fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('Оператор');
  });

  it('handles 403, 404 and an unexpected customer projection safely', () => {
    const fixture = TestBed.createComponent(AppointmentDetailsAdmin); fixture.detectChanges();
    http.expectOne('/api/appointments/order-1').flush(null, { status: 403, statusText: 'Forbidden' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Доступ заборонено');
    fixture.componentInstance.load(); http.expectOne('/api/appointments/order-1').flush(null, { status: 404, statusText: 'Not Found' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Замовлення не знайдено');
    fixture.componentInstance.load(); http.expectOne('/api/appointments/order-1').flush({ id: 'order-1' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Доступ заборонено');
  });

  it('shows a retryable load failure and handles a revoked session during an action', () => {
    const fixture = TestBed.createComponent(AppointmentDetailsAdmin); fixture.detectChanges();
    http.expectOne('/api/appointments/order-1').flush(null, { status: 500, statusText: 'Error' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Не вдалося завантажити замовлення');
    fixture.componentInstance.load(); http.expectOne('/api/appointments/order-1').flush({ ...base, status: 'Scheduled', repairPlan: plan }); fixture.detectChanges();
    fixture.componentInstance.advance(); token();
    http.expectOne('/api/admin/appointments/order-1/transition').flush(null, { status: 403, statusText: 'Forbidden' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Доступ заборонено');
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
  });

  it('cancels a stale details read when the route changes', () => {
    const fixture = TestBed.createComponent(AppointmentDetailsAdmin); fixture.detectChanges();
    const old = http.expectOne('/api/appointments/order-1'); params.next(convertToParamMap({ id: 'order-2' }));
    expect(old.cancelled).toBe(true); http.expectOne('/api/appointments/order-2').flush({ ...base, id: 'order-2', model: 'Нове' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Нове');
  });
});
