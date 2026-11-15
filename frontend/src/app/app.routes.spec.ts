import { routes } from './app.routes';

describe('main route contract', () => {
  it('has each of the twenty agreed routes exactly once and no separate report pages', () => {
    const paths = ['', 'login', 'register', 'forgot-password', 'account/book', 'account/appointments', 'account/appointments/:id',
      'account/notifications', 'account/profile', 'admin', 'admin/appointments', 'admin/appointments/:id', 'admin/assignments',
      'admin/inspection-calendar', 'admin/employees', 'admin/prices', 'admin/users', 'admin/notifications', 'admin/profile', 'admin/reports'];
    for (const path of paths) expect(routes.filter(route => route.path === path)).toHaveLength(1);
    expect(routes.some(route => route.path === 'admin/audit-log' || route.path === 'admin/finance')).toBe(false);
    expect(routes.find(route => route.path === 'admin/reports')?.data?.['role']).toBe('SuperAdmin');
  });
});
