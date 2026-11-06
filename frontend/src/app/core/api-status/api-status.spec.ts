import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiStatus } from './api-status';

describe('ApiStatus', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [ApiStatus],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }));

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('shows loading then the successful API response', async () => {
    const fixture = TestBed.createComponent(ApiStatus);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Перевіряємо з’єднання');
    TestBed.inject(HttpTestingController).expectOne('/api/health').flush({ status: 'Healthy' });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('З’єднання із сервісом встановлено.');
  });

  it('shows a network error and retries successfully', async () => {
    const fixture = TestBed.createComponent(ApiStatus);
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/health').error(new ProgressEvent('error'));
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain('Не вдалося підключитися');
    fixture.nativeElement.querySelector('button').click();
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Перевіряємо з’єднання');
    http.expectOne('/api/health').flush({ status: 'Healthy' });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('З’єднання із сервісом встановлено.');
  });

  it('does not report an unexpected status as healthy', async () => {
    const fixture = TestBed.createComponent(ApiStatus);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/health').flush({ status: 'Unhealthy' });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain('Не вдалося підключитися');
  });
});
