import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { AuthService, bookingReturn, destination } from './auth';

export const authGuard: CanActivateFn = (route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.getSession().pipe(
    map(session => {
      const booking = state.url.split('?')[0] === '/account/book';
      if (!session) return booking ? router.createUrlTree(['/login'], { queryParams: { returnTo: '/account/book' } }) : router.parseUrl('/login');
      if (session.mustChangePassword) return router.parseUrl(destination(session, booking));
      if (route.data['area'] === 'admin' && !session.roles.some(role => role === 'Admin' || role === 'SuperAdmin'))
        return router.parseUrl('/account/appointments');
      if (route.data['role'] === 'SuperAdmin' && !session.roles.includes('SuperAdmin'))
        return router.parseUrl('/admin');
      return true;
    }),
    catchError(() => of(router.createUrlTree(['/login'], { queryParams: { session: 'unavailable', ...(state.url.split('?')[0] === '/account/book' ? { returnTo: '/account/book' } : {}) } }))),
  );
};
export const temporaryPasswordGuard: CanActivateFn = route => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.getSession().pipe(
    map(session => !session ? router.createUrlTree(['/login'], { queryParams: bookingReturn(route.queryParamMap.get('returnTo')) ? { returnTo: '/account/book' } : {} }) : session.mustChangePassword ? true : router.parseUrl(destination(session, bookingReturn(route.queryParamMap.get('returnTo'))))),
    catchError(() => of(router.createUrlTree(['/login'], { queryParams: { session: 'unavailable', ...(bookingReturn(route.queryParamMap.get('returnTo')) ? { returnTo: '/account/book' } : {}) } }))),
  );
};

// Wait for the server's session result before rendering a guest-only page.
export const guestGuard: CanActivateFn = route => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.getSession().pipe(
    map(session => session ? router.parseUrl(destination(session, bookingReturn(route.queryParamMap.get('returnTo')))) : true),
    // The login page can report connectivity errors; do not redirect back to itself.
    catchError(() => of(true)),
  );
};
