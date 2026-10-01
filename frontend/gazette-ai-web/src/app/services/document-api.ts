import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface UploadDocumentResponse {
  message: string;
  documentId: string;
  fileName: string;
  totalPages: number;
  totalChunks: number;
  embeddingDimensions: number;
  status: string;
  uploadedAt: string;
}

export interface DocumentSummary {
  documentId: string;
  fileName: string;
  contentType: string;
  fileSize: number;
  totalPages: number;
  totalChunks: number;
  status: string;
  uploadedAt: string;
  conversationCount: number;
}

export interface ConversationSummary {
  conversationId: string;
  title: string;
  messageCount: number;
  updatedAt: string;
}

export interface StoredChatMessage {
  messageId: string;
  role: 'User' | 'Assistant';
  content: string;
  createdAt: string;
}

export interface ConversationMessagesResponse {
  documentId: string;
  conversationId: string;
  messages: StoredChatMessage[];
}

/*
 * Uploaded PDF document source.
 */
export interface AnswerSource {
  pageNumber: number;
  chunkIndex: number;
  similarityScore: number;
}

/*
 * Tavily live web search source.
 */
export interface WebSource {
  title: string;
  url: string;
}

export type ChatIntent =
  | 'DocumentQuestion'
  | 'DocumentFollowUp'
  | 'Clarification'
  | 'Translation'
  | 'Acknowledgement'
  | 'Greeting'
  | 'CasualConversation'
  | 'GeneralQuestion'
  | 'GeneralPermissionGranted'
  | 'GeneralPermissionDenied'
  | 'OutOfScope';

export interface AskDocumentResponse {
  conversationId: string;
  documentId: string;
  fileName: string;
  question: string;

  /*
   * Backend document search-க்கு பயன்படுத்திய
   * standalone question.
   *
   * Greeting / acknowledgement / general chat-க்கு
   * empty string ஆக இருக்கலாம்.
   */
  searchQuestion: string;

  /*
   * Latest user message-ன் detected intent.
   */
  intent: ChatIntent;

  /*
   * true என்றால் document embedding +
   * retrieval பயன்படுத்தப்பட்டது.
   */
  requiresDocumentSearch: boolean;

  detectedLanguage: string;
  writingStyle: string;
  responseInstruction: string;

  isFollowUp: boolean;
  clarificationRequest: boolean;
  translationRequest: boolean;

  /*
   * Final AI answer.
   */
  answer: string;

  /*
   * PDF document answer sources.
   *
   * Example:
   * Page 1, chunk 2, similarity score 0.82
   */
  sources: AnswerSource[];

  /*
   * Tavily live web search பயன்படுத்தியிருந்தால்
   * website source links இங்கே வரும்.
   *
   * Example:
   * {
   *   title: "Official Website",
   *   url: "https://..."
   * }
   */
  webSources?: WebSource[];

  /*
   * Backend answer type.
   *
   * Examples:
   * document
   * live-web
   * greeting
   * general
   * translation
   */
  answerSource?: string;

  /*
   * Backend external/web information தேவை என்று
   * indicate செய்தால் இந்த value வரும்.
   */
  requiresExternalKnowledge?: boolean;

  /*
   * User web search permission கொடுத்தாரா என்பதை
   * indicate செய்யும்.
   */
  externalPermissionGranted?: boolean;
}

@Injectable({
  providedIn: 'root'
})
export class DocumentApi {
  private readonly http = inject(HttpClient);

  private readonly apiBaseUrl =
    'http://localhost:5114/api';

  /*
   * Get all uploaded documents.
   */
  getDocuments(): Observable<DocumentSummary[]> {
    return this.http.get<DocumentSummary[]>(
      `${this.apiBaseUrl}/documents`
    );
  }

  /*
   * Get conversations belonging to a document.
   */
  getConversations(
    documentId: string
  ): Observable<ConversationSummary[]> {
    return this.http.get<ConversationSummary[]>(
      `${this.apiBaseUrl}/documents/${documentId}/conversations`
    );
  }

  /*
   * Load messages from a previous conversation.
   */
  getConversationMessages(
    documentId: string,
    conversationId: string
  ): Observable<ConversationMessagesResponse> {
    return this.http.get<ConversationMessagesResponse>(
      `${this.apiBaseUrl}/documents/${documentId}` +
      `/conversations/${conversationId}/messages`
    );
  }

  /*
   * Delete document and related conversations.
   */
  deleteDocument(
    documentId: string
  ): Observable<{
    message: string;
    documentId: string;
  }> {
    return this.http.delete<{
      message: string;
      documentId: string;
    }>(
      `${this.apiBaseUrl}/documents/${documentId}`
    );
  }

  /*
   * Upload PDF document.
   */
  uploadDocument(
    file: File
  ): Observable<UploadDocumentResponse> {
    const formData = new FormData();

    formData.append('file', file);

    return this.http.post<UploadDocumentResponse>(
      `${this.apiBaseUrl}/documents/upload`,
      formData
    );
  }

  /*
   * Ask a question.
   *
   * Backend decides whether to use:
   * - Document RAG
   * - Normal conversation
   * - Translation / clarification
   * - Tavily live web search
   */
  askDocument(
    documentId: string,
    question: string,
    conversationId: string | null
  ): Observable<AskDocumentResponse> {
    return this.http.post<AskDocumentResponse>(
      `${this.apiBaseUrl}/questions/ask`,
      {
        documentId,
        question,
        conversationId
      }
    );
  }
}