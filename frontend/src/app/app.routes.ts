import { Routes } from '@angular/router';
import { PublicShell } from './layout/public-shell/public-shell';
import { WorkspaceShell } from './layout/workspace-shell/workspace-shell';
import { Login } from './features/auth/login';
import { Register } from './features/auth/register';
import { ForgotPassword } from './features/auth/forgot-password';
import { ChangePasswordRequired } from './features/auth/change-password-required';
import { Profile } from './features/profile/profile';
import { Booking } from './features/booking/booking/booking';
import { BookingSuccess } from './features/booking/booking-success/booking-success';
import { AppointmentList } from './features/appointments/appointment-list/appointment-list';
import { AppointmentDetail } from './features/appointments/appointment-detail/appointment-detail';
import { AppointmentDetailsAdmin } from './features/admin/appointment-details/appointment-details';
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
  { path: 'account/book/success', component: BookingSuccess, canActivate: [authGuard], data: { area: 'account' }, title: 'Запис підтверджено — AutoBodyService' },
  { path: 'account/book', component: Booking, canActivate: [authGuard], data: { area: 'account' }, title: 'Запис на огляд — AutoBodyService' },
  { path: 'account/appointments', component: AppointmentList, canActivate: [authGuard], data: { area: 'account' }, title: 'Мої записи — AutoBodyService' },
  { path: 'account/appointments/:id', component: AppointmentDetail, canActivate: [authGuard], data: { area: 'account' }, title: 'Деталі запису — AutoBodyService' },
  { path: 'admin/appointments/:id', component: AppointmentDetailsAdmin, canActivate: [authGuard], data: { area: 'admin' }, title: 'Замовлення — AutoBodyService' },
  { path: 'admin/inspection-calendar', component: InspectionCalendar, canActivate: [authGuard], data: { area: 'admin' }, title: 'Календар оглядів — AutoBodyService' },
  { path: 'admin', component: WorkspaceShell, canActivate: [authGuard], data: { area: 'admin' }, title: 'Панель керування — AutoBodyService' },
];
