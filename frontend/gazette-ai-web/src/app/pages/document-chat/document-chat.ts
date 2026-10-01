import {
  ChangeDetectorRef,
  Component,
  OnInit,
  inject
} from '@angular/core';

import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';

import {
  AnswerSource,
  WebSource,
  ConversationSummary,
  DocumentApi,
  DocumentSummary,
  UploadDocumentResponse
} from '../../services/document-api';

import { AuthService } from '../../services/auth';


/*
 * One chat message shown in the UI.
 *
 * sources     -> PDF / RAG sources
 * webSources  -> Tavily live web sources
 */
interface ChatMessage {
  id: number;
  role: 'user' | 'assistant';
  content: string;
  sources?: AnswerSource[];
  webSources?: WebSource[];
}


@Component({
  selector: 'app-document-chat',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './document-chat.html',
  styleUrl: './document-chat.scss'
})
export class DocumentChat implements OnInit {

  private readonly documentApi = inject(DocumentApi);

  private readonly changeDetector =
    inject(ChangeDetectorRef);

  private readonly authService =
    inject(AuthService);

  private readonly router =
    inject(Router);


  private messageId = 0;

  private conversationId: string | null = null;


  readonly currentUser =
    this.authService.currentUser;


  documents: DocumentSummary[] = [];

  conversations: ConversationSummary[] = [];

  selectedFile: File | null = null;

  uploadedDocument:
    UploadDocumentResponse | null = null;

  question = '';

  messages: ChatMessage[] = [];


  isLoadingDocuments = false;

  isLoadingConversations = false;

  isLoadingHistory = false;

  isUploading = false;

  isAsking = false;

  deletingDocumentId: string | null = null;


  errorMessage = '';

  questionError = '';


  /*
   * Component startup.
   */
  ngOnInit(): void {
    this.loadDocuments();
  }


  /*
   * Load user's uploaded documents.
   */
  loadDocuments(): void {

    this.isLoadingDocuments = true;

    this.errorMessage = '';


    this.documentApi
      .getDocuments()
      .subscribe({

        next: documents => {

          this.documents = documents;

          this.isLoadingDocuments = false;

          this.changeDetector.markForCheck();

        },


        error: error => {

          this.errorMessage =
            this.getErrorMessage(
              error,
              'Could not load your documents.'
            );

          this.isLoadingDocuments = false;

          this.changeDetector.markForCheck();

        }

      });

  }


  /*
   * Handle PDF file selection.
   */
  onFileSelected(event: Event): void {

    const input =
      event.target as HTMLInputElement;


    this.selectedFile =
      input.files?.[0] ?? null;


    this.errorMessage = '';

    this.questionError = '';


    this.changeDetector.markForCheck();

  }


  /*
   * Upload and process PDF.
   */
  uploadDocument(): void {

    if (!this.selectedFile) {

      this.errorMessage =
        'Please select a PDF file.';

      return;

    }


    this.isUploading = true;

    this.errorMessage = '';


    this.documentApi
      .uploadDocument(this.selectedFile)
      .subscribe({

        next: response => {

          this.uploadedDocument = response;

          this.selectedFile = null;

          this.isUploading = false;


          this.resetConversation();


          this.addMessage(
            'assistant',
            `Document ready. You can now ask questions about ${response.fileName}.`
          );


          this.loadDocuments();

          this.loadConversations(
            response.documentId
          );


          this.changeDetector.markForCheck();

        },


        error: error => {

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


  /*
   * Open an already uploaded document.
   */
  openDocument(
    document: DocumentSummary
  ): void {

    this.uploadedDocument = {

      message: '',

      documentId:
        document.documentId,

      fileName:
        document.fileName,

      totalPages:
        document.totalPages,

      totalChunks:
        document.totalChunks,

      embeddingDimensions: 768,

      status:
        document.status,

      uploadedAt:
        document.uploadedAt

    };


    this.resetConversation();


    this.addMessage(
      'assistant',
      `Opened ${document.fileName}. Choose a previous chat or ask a new question.`
    );


    this.loadConversations(
      document.documentId
    );


    this.changeDetector.markForCheck();

  }


  /*
   * Load conversation list for current document.
   */
  loadConversations(
    documentId: string
  ): void {

    this.isLoadingConversations = true;

    this.conversations = [];


    this.documentApi
      .getConversations(documentId)
      .subscribe({

        next: conversations => {

          this.conversations =
            conversations;


          this.isLoadingConversations =
            false;


          this.changeDetector.markForCheck();

        },


        error: error => {

          this.errorMessage =
            this.getErrorMessage(
              error,
              'Could not load conversation history.'
            );


          this.isLoadingConversations =
            false;


          this.changeDetector.markForCheck();

        }

      });

  }


  /*
   * Open old conversation.
   */
  openConversation(
    conversation: ConversationSummary
  ): void {

    if (!this.uploadedDocument) {
      return;
    }


    this.isLoadingHistory = true;

    this.questionError = '';


    this.documentApi
      .getConversationMessages(
        this.uploadedDocument.documentId,
        conversation.conversationId
      )
      .subscribe({

        next: response => {

          this.conversationId =
            response.conversationId;


          /*
           * Historical messages currently contain
           * message content only.
           *
           * New live responses can additionally show
           * PDF and web sources.
           */
          this.messages =
            response.messages.map(
              message => ({

                id: ++this.messageId,

                role:
                  message.role.toLowerCase() === 'user'
                    ? 'user'
                    : 'assistant',

                content:
                  message.content

              })
            );


          this.isLoadingHistory = false;


          this.changeDetector.markForCheck();

        },


        error: error => {

          this.questionError =
            this.getErrorMessage(
              error,
              'Could not open this conversation.'
            );


          this.isLoadingHistory = false;


          this.changeDetector.markForCheck();

        }

      });

  }


  /*
   * Delete uploaded document.
   */
  deleteDocument(
    document: DocumentSummary
  ): void {

    const confirmed =
      window.confirm(
        `Delete ${document.fileName}? Its chat history will also be deleted.`
      );


    if (!confirmed) {
      return;
    }


    this.deletingDocumentId =
      document.documentId;


    this.errorMessage = '';


    this.documentApi
      .deleteDocument(
        document.documentId
      )
      .subscribe({

        next: () => {

          this.documents =
            this.documents.filter(
              item =>
                item.documentId !==
                document.documentId
            );


          if (
            this.uploadedDocument
              ?.documentId ===
            document.documentId
          ) {

            this.uploadedDocument = null;

            this.conversations = [];

            this.resetConversation();

          }


          this.deletingDocumentId = null;


          this.changeDetector.markForCheck();

        },


        error: error => {

          this.errorMessage =
            this.getErrorMessage(
              error,
              'Could not delete the document.'
            );


          this.deletingDocumentId = null;


          this.changeDetector.markForCheck();

        }

      });

  }


  /*
   * Question input.
   */
  onQuestionInput(
    event: Event
  ): void {

    const input =
      event.target as HTMLInputElement;


    this.question =
      input.value;


    this.questionError = '';


    this.changeDetector.markForCheck();

  }


  /*
   * Send question to backend.
   *
   * Backend may answer using:
   *
   * 1. PDF RAG
   * 2. Conversation
   * 3. Translation
   * 4. Clarification
   * 5. Tavily live web search
   */
  askQuestion(): void {

    if (!this.uploadedDocument) {

      this.questionError =
        'First upload or open a PDF document.';

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


    /*
     * Show user's message immediately.
     */
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
        this.uploadedDocument.documentId,
        currentQuestion,
        this.conversationId
      )
      .subscribe({

        next: response => {

          /*
           * Keep conversation context.
           */
          this.conversationId =
            response.conversationId;


          /*
           * Add assistant response.
           *
           * response.sources
           *     -> PDF RAG sources
           *
           * response.webSources
           *     -> Tavily web sources
           */
          this.addMessage(
            'assistant',
            response.answer,
            response.sources,
            response.webSources
          );


          this.isAsking = false;


          /*
           * Refresh history sidebar.
           */
          this.loadConversations(
            this.uploadedDocument!.documentId
          );


          this.changeDetector.markForCheck();

        },


        error: error => {

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
   * Start fresh conversation.
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


  /*
   * Logout.
   */
  logout(): void {

    this.authService.logout();

    void this.router.navigate([
      '/auth'
    ]);

  }


  /*
   * Human-readable file size.
   */
  formatFileSize(
    bytes: number
  ): string {

    if (
      bytes <
      1024 * 1024
    ) {

      return `${(
        bytes / 1024
      ).toFixed(1)} KB`;

    }


    return `${(
      bytes /
      (1024 * 1024)
    ).toFixed(1)} MB`;

  }


  /*
   * Reset current conversation state.
   */
  private resetConversation(): void {

    this.conversationId = null;

    this.messages = [];

    this.messageId = 0;

    this.question = '';

    this.questionError = '';

  }


  /*
   * Add message to UI.
   *
   * PDF answer:
   * sources populated.
   *
   * Tavily answer:
   * webSources populated.
   *
   * Normal conversation:
   * both can be empty.
   */
  private addMessage(
    role: 'user' | 'assistant',
    content: string,
    sources?: AnswerSource[],
    webSources?: WebSource[]
  ): void {

    this.messages.push({

      id: ++this.messageId,

      role,

      content,

      sources,

      webSources

    });

  }


  /*
   * Convert backend HTTP errors
   * into user-friendly text.
   */
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


    if (
      typeof error?.error ===
      'string'
    ) {

      return error.error;

    }


    return defaultMessage;

  }

}