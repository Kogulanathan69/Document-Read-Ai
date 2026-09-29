import { Component } from '@angular/core';
import { DocumentChat } from './pages/document-chat/document-chat';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [DocumentChat],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {}