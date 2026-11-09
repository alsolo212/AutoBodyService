import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { AuthService, destination } from './auth';

export const authGuard: CanActivateFn = route => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.getSession().pipe(
    map(session => {
      if (!session) return router.parseUrl('/login');
      if (session.mustChangePassword) return router.parseUrl('/account/change-temporary-password');
      if (route.data['area'] === 'admin' && !session.roles.some(role => role === 'Admin' || role === 'SuperAdmin'))
        return router.parseUrl('/account/appointments');
      return true;
    }),
    catchError(() => of(router.createUrlTree(['/login'], { queryParams: { session: 'unavailable' } }))),
  );
};
export const temporaryPasswordGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.getSession().pipe(
    map(session => !session ? router.parseUrl('/login') : session.mustChangePassword ? true : router.parseUrl(destination(session))),
    catchError(() => of(router.createUrlTree(['/login'], { queryParams: { session: 'unavailable' } }))),
  );
};
