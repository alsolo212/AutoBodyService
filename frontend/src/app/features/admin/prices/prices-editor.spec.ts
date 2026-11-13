import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { decimalLiteral, PricesEditor } from './prices-editor';

const item = { id: 'id-1', name: 'Ремонт', description: 'Опис', priceExact: '12345678901234567890.12345678', isFrom: true, sortOrder: 2, isPublished: true, isArchived: false, version: 5 };
describe('PricesEditor', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [PricesEditor], providers: [provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function create(items = [item]) {
    const fixture = TestBed.createComponent(PricesEditor); fixture.detectChanges();
    http.expectOne('/api/admin/prices').flush(items); fixture.detectChanges(); return fixture;
  }
  it('accepts exact nonnegative decimal boundaries and rejects rounding or invalid values', () => {
    expect(decimalLiteral('0')).toBe('0');
    expect(decimalLiteral('0001,2300')).toBe('1.23');
    expect(decimalLiteral('79228162514264337593543950335')).toBe('79228162514264337593543950335');
    expect(decimalLiteral('0.0000000000000000000000000001')).toBe('0.0000000000000000000000000001');
    for (const input of ['-1', '1e3', '1.', '1,2,3', '79228162514264337593543950336', '0.00000000000000000000000000001', '']) expect(decimalLiteral(input)).toBeNull();
  });
  it('creates an exact decimal after validation and antiforgery without converting it to number', () => {
    const fixture = create([]); const editor = fixture.componentInstance;
    expect(fixture.nativeElement.textContent).toContain('Послуг ще не додано');
    editor.save(); expect(editor.form.invalid).toBe(true); http.expectNone('/api/auth/antiforgery');
    editor.form.setValue({ name: ' Послуга ', description: ' Опис ', price: '12345678901234567890,12345678', sortOrder: 5, isFrom: true, isPublished: true, isArchived: false });
    editor.save(); http.expectOne('/api/auth/antiforgery').flush(null);
    const req = http.expectOne('/api/admin/prices');
    expect(req.request.method).toBe('POST'); expect(req.request.body).toContain('"price":12345678901234567890.12345678');
    expect(req.request.body).toContain('"name":"Послуга"');
    req.flush(item); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('Послугу додано'); expect(fixture.nativeElement.textContent).toContain('12345678901234567890,12345678 ₴');
  });
  it('edits publication and order with the current version and preserves the draft on conflict', () => {
    const fixture = create(); const editor = fixture.componentInstance;
    editor.edit(item); editor.form.controls.price.setValue('0'); editor.form.controls.sortOrder.setValue(-3); editor.form.controls.isPublished.setValue(false); editor.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const req = http.expectOne('/api/admin/prices/id-1');
    expect(req.request.method).toBe('PATCH'); expect(JSON.parse(req.request.body)).toMatchObject({ version: 5, price: 0, sortOrder: -3, isPublished: false });
    req.flush(null, { status: 409, statusText: 'Conflict' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Дані послуги змінилися'); expect(editor.form.controls.price.value).toBe('0'); expect(editor.items()).toEqual([item]);
  });
  it('prevents archived publication and reports forbidden loads and server validation errors', () => {
    const fixture = TestBed.createComponent(PricesEditor); fixture.detectChanges();
    http.expectOne('/api/admin/prices').flush(null, { status: 403, statusText: 'Forbidden' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Доступ до прайсу заборонено');
    fixture.componentInstance.load(); http.expectOne('/api/admin/prices').flush([item]); fixture.detectChanges();
    const editor = fixture.componentInstance; editor.edit(item); editor.form.controls.isArchived.setValue(true); editor.save();
    expect(editor.actionError()).toContain('не можна публікувати'); http.expectNone('/api/auth/antiforgery');
    editor.form.controls.isPublished.setValue(false); editor.save(); http.expectOne('/api/auth/antiforgery').flush(null);
    http.expectOne('/api/admin/prices/id-1').flush(null, { status: 400, statusText: 'Bad Request' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Сервер відхилив дані'); expect(editor.form.controls.isArchived.value).toBe(true);
  });
});
