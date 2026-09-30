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
}

export interface AnswerSource {
  pageNumber: number;
  chunkIndex: number;
  similarityScore: number;
}

export type ChatIntent =
  | 'DocumentQuestion'
  | 'DocumentFollowUp'
  | 'Clarification'
  | 'Translation'
  | 'Acknowledgement'
  | 'Greeting'
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
   * Greeting மற்றும் acknowledgement messages-க்கு
   * இது empty string ஆக இருக்கும்.
   */
  searchQuestion: string;

  /*
   * Latest user message-ன் detected intent.
   */
  intent: ChatIntent;

  /*
   * false என்றால் document embedding மற்றும்
   * retrieval நடைபெறவில்லை.
   */
  requiresDocumentSearch: boolean;

  detectedLanguage: string;
  writingStyle: string;
  responseInstruction: string;

  isFollowUp: boolean;
  clarificationRequest: boolean;
  translationRequest: boolean;

  answer: string;

  /*
   * Direct conversational response-க்கு empty array.
   * Document answer-க்கு page sources இருக்கும்.
   */
  sources: AnswerSource[];
}

@Injectable({
  providedIn: 'root'
})
export class DocumentApi {
  private readonly http = inject(HttpClient);

  private readonly apiBaseUrl =
    'http://localhost:5114/api';

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
