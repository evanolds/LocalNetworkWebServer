// Author:
// Evan Thomas Olds
//
// Creation Date:
// November 17, 2014

using System;
using System.Collections.Generic;
using System.IO;

namespace ETOF.IO
{
    /// <summary>
    /// Provides the ability to write to 0 or more text writers through one object. Can be 
    /// used with 0 text writers to provide a TextWriter that doesn't write anything.
    /// </summary>
    #if ETOF_PUBLIC
	public 
	#else
	internal 
	#endif
    class MultiTextWriter : TextWriter
    {
        private List<TextWriter> m_writers = new List<TextWriter>();

        /// <summary>
        /// Constructs a MultiTextWriter with 0 writers. Writers can be added dynamically 
        /// after construction.
        /// </summary>
        public MultiTextWriter() { }

        /// <summary>
        /// Adds the writer, provided it is non-null, to the list of writers. If the writer 
        /// parameter is null then no action is taken.
        /// </summary>
        public void Add(TextWriter writer)
        {
            if (null != writer)
            {
                m_writers.Add(writer);
            }
        }

        public override System.Text.Encoding Encoding
        {
            get
            {
                if (m_writers.Count > 0)
                {
                    return m_writers[0].Encoding;
                }
                return System.Text.Encoding.UTF8;
            }
        }

        public override void Write(char value)
        {
            foreach (TextWriter writer in m_writers)
            {
                writer.Write(value);
            }
        }

        public override void Write(string value)
        {
            foreach (TextWriter writer in m_writers)
            {
                writer.Write(value);
            }
        }

        public override void WriteLine()
        {
            foreach (TextWriter writer in m_writers)
            {
                writer.WriteLine();
            }
        }

        public override void WriteLine(string value)
        {
            foreach (TextWriter writer in m_writers)
            {
                writer.WriteLine(value);
            }
        }
    }
}