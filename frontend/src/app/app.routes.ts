import { Routes } from '@angular/router';
import { PublicShell } from './layout/public-shell/public-shell';
import { WorkspaceShell } from './layout/workspace-shell/workspace-shell';
import { Login } from './features/auth/login';
import { Register } from './features/auth/register';
import { ForgotPassword } from './features/auth/forgot-password';
import { ChangePasswordRequired } from './features/auth/change-password-required';
import { Profile } from './features/profile/profile';
import { InspectionCalendar } from './features/admin/inspection-calendar/inspection-calendar';
import { authGuard, guestGuard, temporaryPasswordGuard } from './core/auth-guards';
export const routes: Routes = [
  { path: '', component: PublicShell, title: 'AutoBodyService' },
  { path: 'login', component: Login, canActivate: [guestGuard], title: 'Вхід — AutoBodyService' },
  { path: 'register', component: Register, canActivate: [guestGuard], title: 'Реєстрація — AutoBodyService' },
  { path: 'forgot-password', component: ForgotPassword, title: 'Відновлення доступу — AutoBodyService' },
  { path: 'account/change-temporary-password', component: ChangePasswordRequired, canActivate: [temporaryPasswordGuard], title: 'Зміна пароля — AutoBodyService' },
  { path: 'account/profile', component: Profile, canActivate: [authGuard], data: { area: 'account' }, title: 'Мій профіль — AutoBodyService' },
  { path: 'admin/profile', component: Profile, canActivate: [authGuard], data: { area: 'admin' }, title: 'Мій профіль — AutoBodyService' },
  { path: 'account/appointments', component: WorkspaceShell, canActivate: [authGuard], data: { area: 'account' }, title: 'Мої записи — AutoBodyService' },
  { path: 'admin/inspection-calendar', component: InspectionCalendar, canActivate: [authGuard], data: { area: 'admin' }, title: 'Календар оглядів — AutoBodyService' },
  { path: 'admin', component: WorkspaceShell, canActivate: [authGuard], data: { area: 'admin' }, title: 'Панель керування — AutoBodyService' },
];
