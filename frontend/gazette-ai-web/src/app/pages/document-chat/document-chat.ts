import {
  ChangeDetectorRef,
  Component,
  inject
} from '@angular/core';

import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';

import {
  AnswerSource,
  DocumentApi,
  UploadDocumentResponse
} from '../../services/document-api';
import { AuthService } from '../../services/auth';

interface ChatMessage {
  id: number;
  role: 'user' | 'assistant';
  content: string;
  sources?: AnswerSource[];
}

@Component({
  selector: 'app-document-chat',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './document-chat.html',
  styleUrl: './document-chat.scss'
})
export class DocumentChat {
  private readonly documentApi =
    inject(DocumentApi);

  private readonly changeDetector =
    inject(ChangeDetectorRef);

  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);

  private messageId = 0;

  /*
   * Backend generate pannura conversation ID.
   * Follow-up questions ellam same conversation ID-oda
   * backend-kku send aagum.
   */
  private conversationId: string | null = null;

  selectedFile: File | null = null;

  uploadedDocument:
    UploadDocumentResponse | null = null;

  question = '';

  messages: ChatMessage[] = [];

  isUploading = false;
  isAsking = false;

  errorMessage = '';
  questionError = '';

  readonly currentUser = this.authService.currentUser;

  onFileSelected(event: Event): void {
    const input =
      event.target as HTMLInputElement;

    this.selectedFile =
      input.files && input.files.length > 0
        ? input.files[0]
        : null;

    /*
     * New file select pannina previous document
     * conversation clear aaganum.
     */
    this.resetConversation();

    this.uploadedDocument = null;
    this.question = '';
    this.errorMessage = '';
    this.questionError = '';

    this.changeDetector.markForCheck();
  }

  uploadDocument(): void {
    if (!this.selectedFile) {
      this.errorMessage =
        'Please select a PDF file.';

      return;
    }

    this.isUploading = true;
    this.errorMessage = '';
    this.questionError = '';
    this.uploadedDocument = null;

    this.resetConversation();

    this.documentApi
      .uploadDocument(
        this.selectedFile,
        this.requireUserId()
      )
      .subscribe({
        next: response => {
          this.uploadedDocument =
            response;

          this.isUploading = false;

          this.addMessage(
            'assistant',
            `Document ready. You can now ask questions about ${response.fileName}.`
          );

          this.changeDetector.markForCheck();
        },

        error: error => {
          console.error(error);

          this.errorMessage =
            this.getErrorMessage(
              error,
              'Document upload failed.'
            );

          this.isUploading = false;

          this.changeDetector.markForCheck();
        }
      });
  }

  onQuestionInput(event: Event): void {
    const input =
      event.target as HTMLInputElement;

    this.question = input.value;
    this.questionError = '';

    this.changeDetector.markForCheck();
  }

  askQuestion(): void {
    if (!this.uploadedDocument) {
      this.questionError =
        'First upload a PDF document.';

      return;
    }

    if (this.isAsking) {
      return;
    }

    const currentQuestion =
      this.question.trim();

    if (!currentQuestion) {
      this.questionError =
        'Please enter your question.';

      return;
    }

    this.addMessage(
      'user',
      currentQuestion
    );

    this.question = '';
    this.isAsking = true;
    this.questionError = '';

    this.changeDetector.markForCheck();

    this.documentApi
      .askDocument(
        this.requireUserId(),
        this.uploadedDocument.documentId,
        currentQuestion,
        this.conversationId
      )
      .subscribe({
        next: response => {
          /*
           * First question response-la backend
           * conversationId generate pannum.
           *
           * Next questions-ku same ID send pannuvom.
           */
          this.conversationId =
            response.conversationId;

          this.addMessage(
            'assistant',
            response.answer,
            response.sources
          );

          this.isAsking = false;

          this.changeDetector.markForCheck();
        },

        error: error => {
          console.error(error);

          const friendlyError =
            this.getErrorMessage(
              error,
              'Sorry, I could not answer that question.'
            );

          this.addMessage(
            'assistant',
            friendlyError
          );

          this.questionError =
            friendlyError;

          this.isAsking = false;

          this.changeDetector.markForCheck();
        }
      });
  }

  /*
   * Optional:
   * UI-la New Chat button add pannumbothu
   * intha method call pannalam.
   */
  startNewConversation(): void {
    this.resetConversation();

    if (this.uploadedDocument) {
      this.addMessage(
        'assistant',
        `New conversation started for ${this.uploadedDocument.fileName}.`
      );
    }

    this.changeDetector.markForCheck();
  }

  logout(): void {
    this.authService.logout();
    void this.router.navigate(['/auth']);
  }

  private resetConversation(): void {
    this.conversationId = null;
    this.messages = [];
    this.messageId = 0;
    this.question = '';
    this.questionError = '';
  }

  private addMessage(
    role: 'user' | 'assistant',
    content: string,
    sources?: AnswerSource[]
  ): void {
    this.messages.push({
      id: ++this.messageId,
      role,
      content,
      sources
    });
  }

  private requireUserId(): string {
    const userId = this.currentUser()?.userId;

    if (!userId) {
      this.authService.logout();
      void this.router.navigate(['/auth']);
      throw new Error('Authenticated user is unavailable.');
    }

    return userId;
  }

  private getErrorMessage(
    error: any,
    defaultMessage: string
  ): string {
    if (
      typeof error?.error?.message ===
      'string'
    ) {
      return error.error.message;
    }

    if (typeof error?.error === 'string') {
      return error.error;
    }

    return defaultMessage;
  }
}
