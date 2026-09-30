import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

export const routes: Routes = [
  {
    path: 'auth',
    loadComponent: () =>
      import('./pages/auth/auth').then(
        component => component.Auth
      )
  },
  {
    path: 'documents',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/document-chat/document-chat').then(
        component => component.DocumentChat
      )
  },
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'documents'
  },
  {
    path: '**',
    redirectTo: 'documents'
  }
];
