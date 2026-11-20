import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkshopContacts } from './workshop-contacts';

describe('WorkshopContacts', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [WorkshopContacts], providers: [provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());

  it('validates inputs and saves contacts with the current version after antiforgery', () => {
    const fixture = TestBed.createComponent(WorkshopContacts); fixture.detectChanges();
    http.expectOne('/api/admin/workshop-contacts').flush({ phone: null, address: null, version: 0 });
    const component = fixture.componentInstance;
    component.save(); http.expectNone('/api/auth/antiforgery');
    component.form.setValue({ phone: 'invalid', address: 'Адреса' }); component.save(); http.expectNone('/api/auth/antiforgery');
    component.form.setValue({ phone: '000 000 00 01', address: 'Тестова адреса' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/workshop-contacts');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ phone: '000 000 00 01', address: 'Тестова адреса', version: 0 });
    request.flush({ phone: '+380000000001', address: 'Тестова адреса', version: 1 }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Контакти збережено');
    expect(component.form.controls.phone.value).toBe('+380000000001');
  });

  it('keeps a draft on conflict and requires a reload before another save', () => {
    const fixture = TestBed.createComponent(WorkshopContacts); fixture.detectChanges();
    http.expectOne('/api/admin/workshop-contacts').flush({ phone: '+380000000001', address: 'Стара адреса', version: 2 });
    const component = fixture.componentInstance; component.form.controls.address.setValue('Нова адреса'); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/workshop-contacts').flush(null, { status: 409, statusText: 'Conflict' });
    expect(component.form.controls.address.value).toBe('Нова адреса');
    component.save(); http.expectNone('/api/auth/antiforgery');
    component.load(); http.expectOne('/api/admin/workshop-contacts').flush({ phone: '+380000000002', address: 'Інша адреса', version: 3 });
    expect(component.form.controls.address.value).toBe('Інша адреса'); expect(component.conflict()).toBe(false);
  });
});
