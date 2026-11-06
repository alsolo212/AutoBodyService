import { Routes } from '@angular/router';
import { PublicShell } from './layout/public-shell/public-shell';
import { WorkspaceShell } from './layout/workspace-shell/workspace-shell';

export const routes: Routes = [
  { path: '', component: PublicShell, title: 'AutoBodyService' },
  { path: 'account/appointments', component: WorkspaceShell, data: { area: 'account' }, title: 'Мої записи — AutoBodyService' },
  { path: 'admin', component: WorkspaceShell, data: { area: 'admin' }, title: 'Панель керування — AutoBodyService' },
];
