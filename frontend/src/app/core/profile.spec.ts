import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProfileService } from './profile';

describe('ProfileService', () => {
  let service: ProfileService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ProfileService); http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  it('reads the current profile without supplying an account identifier', () => {
    let name = '';
    service.get().subscribe(profile => name = profile.name);
    const request = http.expectOne('/api/profile');
    expect(request.request.method).toBe('GET');
    request.flush({ name: 'Test', phone: '+380000000000' });
    expect(name).toBe('Test');
  });
  it('obtains an antiforgery token and sends only the new name', () => {
    service.updateName('Updated').subscribe();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/profile');
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ name: 'Updated' });
    request.flush(null);
  });
});
