import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiHealth, ApiHealthStatusResponse } from './api-health';
import { appConfig } from '../app.config';

describe('ApiHealth', () => {
  let service: ApiHealth;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ApiHealth);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
  });

  it('getStatus_WhenApiResponds_ReturnsHealthStatus', () => {
    const expected: ApiHealthStatusResponse = { status: 'Healthy' };
    let actual: ApiHealthStatusResponse | undefined;

    service.getStatus().subscribe((response) => {
      actual = response;
    });

    const request = httpTesting.expectOne('/api/health');
    expect(request.request.method).toBe('GET');

    request.flush(expected);

    expect(actual).toEqual(expected);
  });
});

describe('HTTP XSRF configuration', () => {
  it('sends the ASP.NET antiforgery header for a same-origin mutation', () => {
    TestBed.configureTestingModule({ providers: [...appConfig.providers, provideHttpClientTesting()] });
    const httpTesting = TestBed.inject(HttpTestingController);
    document.cookie = 'XSRF-TOKEN=test-value; path=/';
    try {
      TestBed.inject(HttpClient).post('/api/example', {}).subscribe();
      const request = httpTesting.expectOne('/api/example');
      expect(request.request.headers.get('RequestVerificationToken')).toBe('test-value');
      request.flush({});
      httpTesting.verify();
    } finally {
      document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/';
    }
  });
});
