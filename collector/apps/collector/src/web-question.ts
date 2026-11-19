// The Web channel sends the approved question as it is. Until 2026-11-16 a fixed instruction was
// put before it; that prefix is gone. Every send still goes through this one function, so
// approval and attempt identity keep the original and a reused task never accumulates text.
export function buildWebQuestion(questionText: string): string {
  return questionText;
}

export function isWebQuestionEcho(answerText: string, questionText: string): boolean {
  return answerText.trim() === questionText.trim();
}
