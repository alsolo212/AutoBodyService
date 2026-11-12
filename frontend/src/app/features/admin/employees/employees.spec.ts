import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Employees } from './employees';
import { Employee } from './employees-api';

const existing: Employee = { id: 'employee-1', fullName: 'Приклад', specialization: 'Кузовний ремонт', isActive: true, version: 12 };

describe('Employees', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [Employees], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function create(items: Employee[] = []) {
    const fixture = TestBed.createComponent(Employees); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо працівників');
    http.expectOne('/api/admin/employees').flush(items); fixture.detectChanges();
    return fixture;
  }
  it('renders empty and inactive states without inventing assignment data', () => {
    const fixture = create();
    expect(fixture.nativeElement.textContent).toContain('Працівників ще не додано');
    fixture.componentInstance.load();
    http.expectOne('/api/admin/employees').flush([{ ...existing, isActive: false }]); fixture.detectChanges();
    const card: HTMLElement = fixture.nativeElement.querySelector('.employee-card');
    expect(card.textContent).toContain('Неактивний');
    expect(card.textContent).toContain('Дані про призначення ще недоступні');
    expect(fixture.nativeElement.querySelector('a[href="/admin/assignments"]')).not.toBeNull();
  });
  it('validates fields and creates a worker only after fetching the antiforgery token', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.form.setValue({ fullName: '  ', specialization: 'Repair' }); component.save();
    expect(component.form.controls.fullName.invalid).toBe(true); http.expectNone('/api/auth/antiforgery');
    component.form.setValue({ fullName: 'Name', specialization: 'x'.repeat(201) }); component.save();
    expect(component.form.controls.specialization.invalid).toBe(true); http.expectNone('/api/auth/antiforgery');
    component.form.setValue({ fullName: '  Приклад  ', specialization: ' Кузовний ремонт ' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/employees');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ fullName: 'Приклад', specialization: 'Кузовний ремонт', isActive: true });
    request.flush(existing); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Працівника додано');
    expect(component.employees()).toEqual([existing]);
  });
  it('edits using the current version and retains the draft when the server reports a conflict', () => {
    const fixture = create([existing]); const component = fixture.componentInstance;
    component.edit(existing); component.form.setValue({ fullName: 'Нове ім’я', specialization: 'Маляр' }); component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const update = http.expectOne('/api/admin/employees/employee-1');
    expect(update.request.method).toBe('PATCH');
    expect(update.request.body).toEqual({ version: 12, fullName: 'Нове ім’я', specialization: 'Маляр' });
    update.flush(null, { status: 409, statusText: 'Conflict' }); fixture.detectChanges();
    expect(component.actionError()).toContain('змінилися');
    expect(component.form.controls.fullName.value).toBe('Нове ім’я');
    expect(component.employees()).toEqual([existing]);
  });
  it('confirms deactivation before sending and renders the server response', () => {
    const fixture = create([existing]); const component = fixture.componentInstance;
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    dialog.showModal = vi.fn(); dialog.close = vi.fn();
    component.confirmDeactivation(existing); fixture.detectChanges();
    expect(dialog.showModal).toHaveBeenCalledOnce(); http.expectNone('/api/auth/antiforgery');
    (dialog.querySelector('.button--secondary') as HTMLButtonElement).click(); http.expectNone('/api/auth/antiforgery');
    component.confirmDeactivation(existing); fixture.detectChanges();
    (dialog.querySelector('.button--danger') as HTMLButtonElement).click();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/employees/employee-1');
    expect(request.request.body).toEqual({ version: 12, isActive: false });
    request.flush({ ...existing, isActive: false, version: 13 }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Неактивний');
    expect(fixture.nativeElement.textContent).toContain('Працівника деактивовано');
  });
  it('reports forbidden loads, retries and reports a server validation error without losing input', () => {
    const fixture = TestBed.createComponent(Employees); fixture.detectChanges();
    http.expectOne('/api/admin/employees').flush(null, { status: 403, statusText: 'Forbidden' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Доступ до працівників заборонено');
    fixture.componentInstance.load(); http.expectOne('/api/admin/employees').flush([]); fixture.detectChanges();
    fixture.componentInstance.form.setValue({ fullName: 'Name', specialization: 'Repair' }); fixture.componentInstance.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/employees').flush(null, { status: 400, statusText: 'Bad Request' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Сервер відхилив дані');
    expect(fixture.componentInstance.form.controls.fullName.value).toBe('Name');
  });
});
