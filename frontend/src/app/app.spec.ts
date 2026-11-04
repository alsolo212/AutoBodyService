import { of } from 'rxjs';
import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { ApiHealth } from './core/api-health';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        {
          provide: ApiHealth,
          useValue: {
            getStatus: () => of({ status: 'Healthy' }),
          },
        },
      ],
    })
      .compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render api status after health check', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('[data-testid="api-status"]')?.textContent).toContain('API: Healthy');
  });
});
