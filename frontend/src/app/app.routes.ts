import { Routes } from '@angular/router';
import { authGuard } from './core/auth';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/login/login').then((m) => m.Login), title: 'Sign in · TradeBot' },
  { path: '', canActivate: [authGuard], loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard), title: 'TradeBot' },
  { path: '**', redirectTo: '' },
];
