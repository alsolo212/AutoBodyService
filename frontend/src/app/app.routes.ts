import { Routes } from '@angular/router';
import { PublicShell } from './layout/public-shell/public-shell';
import { WorkspaceShell } from './layout/workspace-shell/workspace-shell';
import { Login } from './features/auth/login';
import { Register } from './features/auth/register';
import { ForgotPassword } from './features/auth/forgot-password';
import { ChangePasswordRequired } from './features/auth/change-password-required';
import { authGuard, temporaryPasswordGuard } from './core/auth-guards';
export const routes: Routes = [
  { path: '', component: PublicShell, title: 'AutoBodyService' },
  { path: 'login', component: Login, title: 'Вхід — AutoBodyService' },
  { path: 'register', component: Register, title: 'Реєстрація — AutoBodyService' },
  { path: 'forgot-password', component: ForgotPassword, title: 'Відновлення доступу — AutoBodyService' },
  { path: 'account/change-temporary-password', component: ChangePasswordRequired, canActivate: [temporaryPasswordGuard], title: 'Зміна пароля — AutoBodyService' },
  { path: 'account/appointments', component: WorkspaceShell, canActivate: [authGuard], data: { area: 'account' }, title: 'Мої записи — AutoBodyService' },
  { path: 'admin', component: WorkspaceShell, canActivate: [authGuard], data: { area: 'admin' }, title: 'Панель керування — AutoBodyService' },
];
