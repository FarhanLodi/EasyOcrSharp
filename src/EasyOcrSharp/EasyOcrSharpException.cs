using System;
using System.Runtime.Serialization;

namespace EasyOcrSharp;

/// <summary>
/// Represents errors that occur while working with the EasyOcrSharp library.
/// </summary>
[Serializable]
public class EasyOcrSharpException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EasyOcrSharpException"/> class.
    /// </summary>
    public EasyOcrSharpException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyOcrSharpException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public EasyOcrSharpException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyOcrSharpException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
    public EasyOcrSharpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyOcrSharpException"/> class with serialized data.
    /// </summary>
    /// <param name="info">The <see cref="SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
    /// <param name="context">The <see cref="StreamingContext"/> that contains contextual information about the source or destination.</param>
    [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.")]
    protected EasyOcrSharpException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}

