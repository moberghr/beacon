using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Semantico.Core.Adapters.Mail
{
    public interface IEmailAdapter
    {
        public Task SendEmailAsync(string to, string subject, string body);
    }
}
