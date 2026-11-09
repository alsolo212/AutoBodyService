import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { CancelAppointment } from './cancel-appointment';

describe('CancelAppointment', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [CancelAppointment], providers: [provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => { http.verify(); vi.useRealTimers(); });
  function setup() {
    const fixture = TestBed.createComponent(CancelAppointment);
    fixture.componentRef.setInput('id', 'order-1'); fixture.componentRef.setInput('isOwn', true);
    fixture.componentRef.setInput('status', 'AwaitingInspection'); fixture.componentRef.setInput('inspectionStartUtc', '2099-01-01T10:00:00Z');
    fixture.detectChanges(); return fixture;
  }
  it('shows the action only for an owned future appointment awaiting inspection', () => {
    const f = setup(); expect(f.nativeElement.querySelector('summary')).not.toBeNull();
    f.componentRef.setInput('isOwn', false); f.detectChanges(); expect(f.nativeElement.querySelector('summary')).toBeNull();
    f.componentRef.setInput('isOwn', true); f.componentRef.setInput('status', 'Scheduled'); f.detectChanges(); expect(f.nativeElement.querySelector('summary')).toBeNull();
    f.componentRef.setInput('status', 'AwaitingInspection'); f.componentRef.setInput('inspectionStartUtc', '2020-01-01T00:00:00Z'); f.detectChanges(); expect(f.nativeElement.querySelector('summary')).toBeNull();
  });
  it('confirms with antiforgery, prevents duplicate requests and emits only after success', () => {
    const f = setup(); const component = f.componentInstance; const output = vi.fn(); component.cancelled.subscribe(output);
    const dialog = f.nativeElement.querySelector('dialog') as HTMLDialogElement; dialog.showModal = vi.fn(); dialog.close = vi.fn();
    (f.nativeElement.querySelector('.actions button') as HTMLButtonElement).click(); expect(dialog.showModal).toHaveBeenCalledOnce();
    http.expectNone('/api/auth/antiforgery');
    (f.nativeElement.querySelector('.dialog-actions .button--secondary') as HTMLButtonElement).click(); http.expectNone('/api/auth/antiforgery');
    (f.nativeElement.querySelector('.dialog-actions .button--danger') as HTMLButtonElement).click(); component.confirm();
    http.expectOne('/api/auth/antiforgery').flush(null); const request = http.expectOne('/api/appointments/order-1/cancel');
    expect(request.request.method).toBe('POST'); expect(output).not.toHaveBeenCalled(); request.flush(null); expect(output).toHaveBeenCalledOnce();
  });
  it('keeps the record and displays a conflict when the server rejects cancellation', () => {
    const f = setup(); const output = vi.fn(); f.componentInstance.cancelled.subscribe(output);
    f.componentInstance.confirm(); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/appointments/order-1/cancel').flush({}, { status: 409, statusText: 'Conflict' }); f.detectChanges();
    expect(output).not.toHaveBeenCalled(); expect(f.nativeElement.textContent).toContain('Запис уже змінився');
  });
  it('hides the action when the inspection time arrives while the page is open', () => {
    vi.useFakeTimers(); vi.setSystemTime(new Date('2099-01-01T09:59:59Z'));
    const f = setup(); expect(f.nativeElement.querySelector('summary')).not.toBeNull();
    vi.advanceTimersByTime(1001); f.detectChanges(); expect(f.nativeElement.querySelector('summary')).toBeNull();
  });
});
