import {
  ChangeDetectorRef,
  Component,
  OnDestroy,
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
export class DocumentChat
  implements OnInit, OnDestroy {

  private readonly documentApi =
    inject(DocumentApi);

  private readonly changeDetector =
    inject(ChangeDetectorRef);

  private readonly authService =
    inject(AuthService);

  private readonly router =
    inject(Router);


  private messageId = 0;

  private conversationId:
    string | null = null;


  /*
   * Background-processing polling.
   *
   * While the selected document is Queued or
   * Processing, the frontend refreshes document
   * status every 2 seconds.
   */
  private pollingTimer:
    ReturnType<typeof setInterval> | null = null;

  private pollingDocumentId:
    string | null = null;


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

  deletingDocumentId:
    string | null = null;


  errorMessage = '';

  questionError = '';


  /*
   * Component startup.
   */
  ngOnInit(): void {
    this.loadDocuments();
  }


  /*
   * Stop timers when user leaves this page.
   */
  ngOnDestroy(): void {
    this.stopDocumentPolling();
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

          /*
           * If a document is currently open,
           * update its status/pages/chunks using
           * the latest backend data.
           */
          this.syncSelectedDocument(
            documents
          );

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
   * Upload PDF.
   *
   * Backend now returns HTTP 202 immediately.
   *
   * The document may initially be:
   *
   * Queued
   *   ↓
   * Processing
   *   ↓
   * Ready
   *
   * Heavy OCR / chunk / embedding work happens
   * in the backend worker.
   */
  uploadDocument(): void {

    if (!this.selectedFile) {

      this.errorMessage =
        'Please select a PDF file.';

      return;
    }

    this.stopDocumentPolling();

    this.isUploading = true;

    this.errorMessage = '';

    this.questionError = '';

    this.documentApi
      .uploadDocument(this.selectedFile)
      .subscribe({

        next: response => {

          this.uploadedDocument =
            response;

          this.selectedFile = null;

          this.isUploading = false;

          this.resetConversation();

          /*
           * Do NOT say "Document ready" here.
           *
           * HTTP 202 only means the upload was
           * accepted for background processing.
           */
          this.addProcessingStatusMessage(
            response.status,
            response.fileName
          );

          /*
           * Refresh sidebar immediately.
           */
          this.loadDocuments();

          /*
           * Conversations should normally be empty
           * for a new document, but keeping this call
           * preserves the existing UI behaviour.
           */
          this.loadConversations(
            response.documentId
          );

          /*
           * Begin polling only when processing
           * has not finished yet.
           */
          if (
            this.isPendingStatus(
              response.status
            )
          ) {
            this.startDocumentPolling(
              response.documentId
            );
          }

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

    this.stopDocumentPolling();

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

    /*
     * Message depends on actual processing status.
     */
    if (
      this.isPendingStatus(
        document.status
      )
    ) {

      this.addProcessingStatusMessage(
        document.status,
        document.fileName
      );

      this.startDocumentPolling(
        document.documentId
      );

    } else if (
      this.isReadyStatus(
        document.status
      )
    ) {

      this.addMessage(
        'assistant',
        `Opened ${document.fileName}. Choose a previous chat or ask a new question.`
      );

    } else if (
      this.isFailedStatus(
        document.status
      )
    ) {

      this.addMessage(
        'assistant',
        `Processing failed for ${document.fileName}. Please delete it and upload the PDF again.`
      );

    } else {

      this.addMessage(
        'assistant',
        `Opened ${document.fileName}.`
      );
    }

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

    /*
     * Questions/history should only be used after
     * document processing has completed.
     */
    if (
      !this.isReadyStatus(
        this.uploadedDocument.status
      )
    ) {

      this.questionError =
        'Please wait until the document is ready.';

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

                id:
                  ++this.messageId,

                role:
                  message.role
                    .toLowerCase() === 'user'
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

    /*
     * If this document is currently being polled,
     * stop polling before deleting it.
     *
     * This prevents the frontend from continuing
     * to request status for a deleted document.
     */
    if (
      this.pollingDocumentId ===
      document.documentId
    ) {
      this.stopDocumentPolling();
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

    /*
     * Prevent questions while background processing
     * is still running.
     */
    if (
      !this.isReadyStatus(
        this.uploadedDocument.status
      )
    ) {

      if (
        this.isFailedStatus(
          this.uploadedDocument.status
        )
      ) {

        this.questionError =
          'Document processing failed. Please upload the PDF again.';

      } else {

        this.questionError =
          'Please wait. The document is still being processed.';
      }

      this.changeDetector.markForCheck();

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
            this.uploadedDocument!
              .documentId
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

      if (
        this.isReadyStatus(
          this.uploadedDocument.status
        )
      ) {

        this.addMessage(
          'assistant',
          `New conversation started for ${this.uploadedDocument.fileName}.`
        );

      } else {

        this.addProcessingStatusMessage(
          this.uploadedDocument.status,
          this.uploadedDocument.fileName
        );
      }
    }

    this.changeDetector.markForCheck();
  }


  /*
   * Logout.
   */
  logout(): void {

    this.stopDocumentPolling();

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
   * Start polling the backend for one document.
   *
   * We refresh immediately and then every 2 seconds.
   */
  private startDocumentPolling(
    documentId: string
  ): void {

    this.stopDocumentPolling();

    this.pollingDocumentId =
      documentId;

    /*
     * First refresh without waiting 2 seconds.
     */
    this.pollDocumentStatus();

    this.pollingTimer =
      setInterval(
        () => {
          this.pollDocumentStatus();
        },
        2000
      );
  }


  /*
   * Stop document-status polling.
   */
  private stopDocumentPolling(): void {

    if (this.pollingTimer !== null) {

      clearInterval(
        this.pollingTimer
      );

      this.pollingTimer = null;
    }

    this.pollingDocumentId = null;
  }


  /*
   * Poll document list.
   *
   * Existing GET /api/Documents is enough for this
   * stage, so no additional backend endpoint is needed.
   */
  private pollDocumentStatus(): void {

    const documentId =
      this.pollingDocumentId;

    if (!documentId) {
      return;
    }

    this.documentApi
      .getDocuments()
      .subscribe({

        next: documents => {

          this.documents =
            documents;

          const latestDocument =
            documents.find(
              document =>
                document.documentId ===
                documentId
            );

          /*
           * Document may have been deleted while
           * processing.
           */
          if (!latestDocument) {

            this.stopDocumentPolling();

            this.changeDetector.markForCheck();

            return;
          }

          /*
           * Only update the active document if the
           * user is still viewing the document being
           * polled.
           */
          if (
            this.uploadedDocument
              ?.documentId ===
            latestDocument.documentId
          ) {

            const previousStatus =
              this.uploadedDocument.status;

            this.updateUploadedDocument(
              latestDocument
            );

            const newStatus =
              latestDocument.status;

            /*
             * Only replace the processing message
             * when the status actually changes.
             */
            if (
              previousStatus
                .toLowerCase() !==
              newStatus.toLowerCase()
            ) {

              this.showProcessingStatusChange(
                newStatus,
                latestDocument.fileName
              );
            }
          }

          /*
           * Processing finished successfully.
           */
          if (
            this.isReadyStatus(
              latestDocument.status
            )
          ) {

            this.stopDocumentPolling();

            /*
             * Ensure final Ready state is visible
             * even if the backend moved very quickly
             * from Queued -> Processing -> Ready.
             */
            if (
              this.uploadedDocument
                ?.documentId ===
              latestDocument.documentId
            ) {

              this.showReadyMessage(
                latestDocument.fileName
              );
            }
          }

          /*
           * Processing failed.
           */
          if (
            this.isFailedStatus(
              latestDocument.status
            )
          ) {

            this.stopDocumentPolling();

            if (
              this.uploadedDocument
                ?.documentId ===
              latestDocument.documentId
            ) {

              this.showFailedMessage(
                latestDocument.fileName
              );
            }
          }

          this.changeDetector.markForCheck();
        },

        error: () => {

          /*
           * A temporary polling failure should not
           * destroy the current UI state.
           *
           * The next polling cycle can retry.
           */
        }

      });
  }


  /*
   * Synchronize selected document whenever the normal
   * document list is refreshed.
   */
  private syncSelectedDocument(
    documents: DocumentSummary[]
  ): void {

    if (!this.uploadedDocument) {
      return;
    }

    const latestDocument =
      documents.find(
        document =>
          document.documentId ===
          this.uploadedDocument!
            .documentId
      );

    if (!latestDocument) {
      return;
    }

    this.updateUploadedDocument(
      latestDocument
    );

    /*
     * If page was refreshed/opened while a document
     * is still processing, make sure polling starts.
     */
    if (
      this.isPendingStatus(
        latestDocument.status
      ) &&
      this.pollingTimer === null
    ) {

      this.startDocumentPolling(
        latestDocument.documentId
      );
    }
  }


  /*
   * Copy latest backend document values into the
   * active UploadDocumentResponse object.
   */
  private updateUploadedDocument(
    document: DocumentSummary
  ): void {

    if (!this.uploadedDocument) {
      return;
    }

    this.uploadedDocument = {
      ...this.uploadedDocument,

      documentId:
        document.documentId,

      fileName:
        document.fileName,

      totalPages:
        document.totalPages,

      totalChunks:
        document.totalChunks,

      status:
        document.status,

      uploadedAt:
        document.uploadedAt
    };
  }


  /*
   * Initial processing message.
   */
  private addProcessingStatusMessage(
    status: string,
    fileName: string
  ): void {

    if (
      this.isReadyStatus(status)
    ) {

      this.addMessage(
        'assistant',
        `Document ready. You can now ask questions about ${fileName}.`
      );

      return;
    }

    if (
      this.isFailedStatus(status)
    ) {

      this.addMessage(
        'assistant',
        `Document processing failed for ${fileName}. Please upload the PDF again.`
      );

      return;
    }

    if (
      status.toLowerCase() ===
      'processing'
    ) {

      this.addMessage(
        'assistant',
        `Processing ${fileName}. Please wait while the document is prepared.`
      );

      return;
    }

    this.addMessage(
      'assistant',
      `${fileName} is queued for processing. Please wait.`
    );
  }


  /*
   * Display status transition.
   */
  private showProcessingStatusChange(
    status: string,
    fileName: string
  ): void {

    if (
      status.toLowerCase() ===
      'processing'
    ) {

      this.replaceProcessingMessage(
        `Processing ${fileName}. Please wait while the document is prepared.`
      );

      return;
    }

    if (
      this.isReadyStatus(status)
    ) {

      this.showReadyMessage(
        fileName
      );

      return;
    }

    if (
      this.isFailedStatus(status)
    ) {

      this.showFailedMessage(
        fileName
      );
    }
  }


  /*
   * Ready message.
   */
  private showReadyMessage(
    fileName: string
  ): void {

    this.replaceProcessingMessage(
      `Document ready. You can now ask questions about ${fileName}.`
    );
  }


  /*
   * Failed message.
   */
  private showFailedMessage(
    fileName: string
  ): void {

    this.replaceProcessingMessage(
      `Document processing failed for ${fileName}. Please upload the PDF again.`
    );
  }


  /*
   * During processing we only need one assistant
   * status message instead of:
   *
   * Queued
   * Processing
   * Ready
   *
   * appearing as three separate chat messages.
   */
  private replaceProcessingMessage(
    content: string
  ): void {

    const assistantMessage =
      this.messages.find(
        message =>
          message.role ===
          'assistant'
      );

    if (assistantMessage) {

      assistantMessage.content =
        content;

    } else {

      this.addMessage(
        'assistant',
        content
      );
    }
  }


  /*
   * Status helpers.
   */
  private isPendingStatus(
    status: string | null | undefined
  ): boolean {

    const normalized =
      status
        ?.trim()
        .toLowerCase();

    return (
      normalized === 'queued' ||
      normalized === 'processing' ||
      normalized === 'pending'
    );
  }


  private isReadyStatus(
    status: string | null | undefined
  ): boolean {

    return (
      status
        ?.trim()
        .toLowerCase() ===
      'ready'
    );
  }


  private isFailedStatus(
    status: string | null | undefined
  ): boolean {

    return (
      status
        ?.trim()
        .toLowerCase() ===
      'failed'
    );
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

      id:
        ++this.messageId,

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