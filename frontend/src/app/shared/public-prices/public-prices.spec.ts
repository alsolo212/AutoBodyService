import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PublicPrices } from './public-prices';

describe('public prices', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [PublicPrices], providers: [provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  it('renders published API data in order with exact decimals, zero and the from label', () => {
    const fixture = TestBed.createComponent(PublicPrices); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо ціни');
    http.expectOne('/api/prices').flush([
      { id: 'first', name: 'Перша', description: 'Опис', price: 0, priceExact: '0', currency: 'UAH', isFrom: true },
      { id: 'second', name: 'Друга', description: '', price: 12345678901234567890, priceExact: '12345678901234567890.12345678', currency: 'UAH', isFrom: false },
    ]); fixture.detectChanges();
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.price-list li')];
    expect(rows).toHaveLength(2); expect(rows[0].textContent).toContain('Перша'); expect(rows[0].textContent).toContain('від 0 ₴');
    expect(rows[1].textContent).toContain('Друга'); expect(rows[1].textContent).toContain('12345678901234567890,12345678 ₴');
    expect(rows[1].textContent).not.toContain('від');
  });
  it('shows a truthful empty state and supports retry after a failed request', () => {
    const fixture = TestBed.createComponent(PublicPrices); fixture.detectChanges();
    http.expectOne('/api/prices').flush(null, { status: 503, statusText: 'Unavailable' }); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role=alert]')?.textContent).toContain('Не вдалося завантажити ціни');
    expect(fixture.nativeElement.textContent).not.toContain('Опублікованих послуг поки немає');
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    http.expectOne('/api/prices').flush([]); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Опублікованих послуг поки немає');
    expect(fixture.nativeElement.querySelector('.price-list')).toBeNull();
  });
});
