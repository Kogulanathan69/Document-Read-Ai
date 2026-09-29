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

export interface AskDocumentResponse {
  conversationId: string;
  documentId: string;
  fileName: string;
  question: string;

  /*
   * Document search-க்கு backend பயன்படுத்திய
   * complete standalone question.
   */
  searchQuestion: string;

  /*
   * AI கண்டுபிடித்த language மற்றும் writing style.
   */
  detectedLanguage: string;
  writingStyle: string;
  responseInstruction: string;

  /*
   * Conversation message type.
   */
  isFollowUp: boolean;
  clarificationRequest: boolean;
  translationRequest: boolean;

  answer: string;
  sources: AnswerSource[];
}

@Injectable({
  providedIn: 'root'
})
export class DocumentApi {
  private readonly http = inject(HttpClient);

  private readonly apiBaseUrl =
    'http://localhost:5000/api';

  uploadDocument(
    file: File,
    userId: string
  ): Observable<UploadDocumentResponse> {
    const formData = new FormData();

    formData.append('file', file);
    formData.append('userId', userId);

    return this.http.post<UploadDocumentResponse>(
      `${this.apiBaseUrl}/documents/upload`,
      formData
    );
  }

  askDocument(
    userId: string,
    documentId: string,
    question: string,
    conversationId: string | null
  ): Observable<AskDocumentResponse> {
    return this.http.post<AskDocumentResponse>(
      `${this.apiBaseUrl}/questions/ask`,
      {
        userId,
        documentId,
        question,
        conversationId
      }
    );
  }
}