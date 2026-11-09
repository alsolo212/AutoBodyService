import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Booking } from './booking';

describe('Booking', () => {
  let http: HttpTestingController;
  const slot = '2099-01-15T08:00:00Z';
  beforeEach(() => { TestBed.configureTestingModule({ imports: [Booking], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function setup() {
    const fixture = TestBed.createComponent(Booking);
    http.expectOne(r => r.url === '/api/inspection-slots').flush({ slots: [] });
    const component = fixture.componentInstance;
    component.form.controls.date.setValue('2099-01-15');
    http.expectOne(r => r.url === '/api/inspection-slots' && r.params.get('date') === '2099-01-15').flush({ slots: [{ startUtc: slot, endUtc: '2099-01-15T08:30:00Z' }] });
    fixture.detectChanges();
    return { fixture, component };
  }
  function fill(component: Booking) {
    component.form.controls.slot.setValue(slot); component.next();
    component.form.patchValue({ vin: '1HGCM82633A004352', make: ' Test ', model: ' Model ', problem: ' Опис ' }); component.next(); component.next();
  }
  it('requires a free slot, valid VIN, vehicle and a nonblank problem before confirmation', () => {
    const { component } = setup(); component.next(); expect(component.step()).toBe(1);
    component.form.controls.slot.setValue(slot); component.next(); expect(component.step()).toBe(2);
    component.form.patchValue({ vin: 'invalid', make: ' ', model: 'Model' }); component.next(); expect(component.step()).toBe(2);
    component.form.patchValue({ vin: '1HGCM82633A004352', make: 'Make' }); component.next(); expect(component.step()).toBe(3);
    component.form.controls.problem.setValue('   '); component.next(); expect(component.step()).toBe(3);
    component.form.controls.problem.setValue('Проблема'); component.next(); expect(component.step()).toBe(4);
  });
  it('posts the selected UTC slot and trimmed details after antiforgery and opens confirmation', () => {
    const { component } = setup(); const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true); fill(component); component.submit();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/appointments');
    expect(request.request.body).toEqual({ inspectionStartUtc: slot, vin: '1HGCM82633A004352', make: 'Test', model: 'Model', problemDescription: 'Опис' });
    request.flush({ id: 'id', inspectionStartUtc: slot, status: 'AwaitingInspection' });
    expect(navigate).toHaveBeenCalledWith(['/account/book/success'], { state: { inspectionStartUtc: slot } });
  });
  it('preserves vehicle details after a conflict and reloads availability for another choice', () => {
    const { component } = setup(); fill(component); component.submit(); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/appointments').flush({}, { status: 409, statusText: 'Conflict' });
    expect(component.step()).toBe(1); expect(component.conflict()).toBe(true); expect(component.form.controls.slot.value).toBe('');
    expect(component.form.controls.problem.value).toBe(' Опис '); expect(component.form.controls.vin.value).toBe('1HGCM82633A004352');
    http.expectOne(r => r.url === '/api/inspection-slots' && r.params.get('date') === '2099-01-15').flush({ slots: [] });
  });
  it('uses Kyiv time for the displayed slot and shows an empty availability message', () => {
    const { fixture, component } = setup(); expect(fixture.nativeElement.textContent).toContain('10:00');
    component.form.controls.date.setValue('2099-01-16'); http.expectOne(r => r.url === '/api/inspection-slots').flush({ slots: [] }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('На цю дату вільного часу немає');
  });
  it('distinguishes repeated Kyiv times and submits the selected UTC instant', () => {
    vi.useFakeTimers(); vi.setSystemTime(new Date('2026-01-01T00:00:00Z'));
    const { fixture, component } = setup();
    component.form.controls.date.setValue('2027-10-31');
    http.expectOne(r => r.url === '/api/inspection-slots').flush({ slots: [
      { startUtc: '2027-10-31T00:00:00Z', endUtc: '2027-10-31T00:30:00Z' },
      { startUtc: '2027-10-31T01:00:00Z', endUtc: '2027-10-31T01:30:00Z' },
    ] }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('03:00 (UTC+03:00)');
    expect(fixture.nativeElement.textContent).toContain('03:00 (UTC+02:00)');
    const second = fixture.nativeElement.querySelectorAll('input[type=radio]')[1] as HTMLInputElement;
    second.checked = true; second.dispatchEvent(new Event('change'));
    expect(component.form.controls.slot.value).toBe('2027-10-31T01:00:00Z'); component.next();
    component.form.patchValue({ vin: '1HGCM82633A004352', make: 'Test', model: 'Model', problem: 'Issue' }); component.next(); component.next(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('03:00 (UTC+02:00)');
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    component.submit(); http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/appointments'); expect(request.request.body.inspectionStartUtc).toBe('2027-10-31T01:00:00Z');
    request.flush({ id: 'id', inspectionStartUtc: '2027-10-31T01:00:00Z', status: 'AwaitingInspection' });
    vi.useRealTimers();
  });

});
