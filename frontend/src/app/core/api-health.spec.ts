import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiHealth, ApiHealthStatusResponse } from './api-health';

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

    const request = httpTesting.expectOne('https://localhost:7097/api/health');
    expect(request.request.method).toBe('GET');

    request.flush(expected);

    expect(actual).toEqual(expected);
  });
});
